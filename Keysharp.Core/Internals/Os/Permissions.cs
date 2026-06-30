using Keysharp.Builtins;
#if LINUX
using Keysharp.Internals.Input.Linux;
using Keysharp.Internals.Window.Linux.Wayland;
using Keysharp.Internals.Os.Unix;
#endif
namespace Keysharp.Internals.Os
{
	internal enum PermissionStatus
	{
		Granted,
		Denied,
		Unsupported,
		NotApplicable
	}

	internal readonly struct PermissionResult(PermissionStatus status, string message = null)
	{
		internal PermissionStatus Status { get; } = status;
		internal string Message { get; } = message ?? string.Empty;
		internal bool IsGranted => Status == PermissionStatus.Granted || Status == PermissionStatus.NotApplicable;
	}

	internal enum FilePermissionAccess
	{
		Read,
		Write,
		ReadWrite,
		Append
	}

	internal interface IPermissionManager
	{
		PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null);
		PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null);
		PermissionResult RequestInputInjection(bool? prompt = null, string operation = null);
		PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null);
		PermissionResult RequestScreenCapture(bool? prompt = null, string operation = null);
		PermissionResult RequestFileAccess(string path, FilePermissionAccess access, bool? prompt = null, string operation = null);

		PermissionResult EnsureAccessibilityAutomation(bool? prompt = null, string operation = null);
		PermissionResult EnsureInputMonitoring(bool? prompt = null, string operation = null);
		PermissionResult EnsureInputInjection(bool? prompt = null, string operation = null);
		PermissionResult EnsureScreenCapture(bool? prompt = null, string operation = null);
		PermissionResult EnsureFileAccess(string path, FilePermissionAccess access, bool? prompt = null, string operation = null);
	}

	internal class DefaultPermissionManager : IPermissionManager
	{
		protected static bool ResolvePrompt(bool? prompt) => Script.IsHeadless ? false : prompt ?? true;

		public virtual PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
		public virtual PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
		public virtual PermissionResult RequestInputInjection(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);

		public virtual PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null)
		{
			PermissionResult result = new(PermissionStatus.NotApplicable);
			if (accessibilityAutomation) result = RequestAccessibilityAutomation(prompt, operation);
			if (monitoring)    result = RequestInputMonitoring(prompt, operation);
			if (injection)     result = RequestInputInjection(prompt, operation);
			if (screenCapture) result = RequestScreenCapture(prompt, operation);
			return result;
		}
		public virtual PermissionResult RequestScreenCapture(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
		public virtual PermissionResult RequestFileAccess(string path, FilePermissionAccess access, bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);

		public virtual PermissionResult EnsureAccessibilityAutomation(bool? prompt = null, string operation = null)
			=> EnsureGranted(RequestAccessibilityAutomation(prompt, operation), operation ?? "accessibility automation");
		public virtual PermissionResult EnsureInputMonitoring(bool? prompt = null, string operation = null)
			=> EnsureGranted(RequestInputMonitoring(prompt, operation), operation ?? "keyboard/mouse monitoring");
		public virtual PermissionResult EnsureInputInjection(bool? prompt = null, string operation = null)
			=> EnsureGranted(RequestInputInjection(prompt, operation), operation ?? "keyboard/mouse sending");
		public virtual PermissionResult EnsureScreenCapture(bool? prompt = null, string operation = null)
			=> EnsureGranted(RequestScreenCapture(prompt, operation), operation ?? "screen capture");
		public virtual PermissionResult EnsureFileAccess(string path, FilePermissionAccess access, bool? prompt = null, string operation = null)
			=> EnsureGranted(RequestFileAccess(path, access, prompt, operation), operation ?? "file access");

		private static PermissionResult EnsureGranted(PermissionResult result, string operation)
		{
			if (result.IsGranted)
				return result;

			throw new InvalidOperationException(result.Message.IsNullOrEmpty()
				? $"Permission is required for '{operation}'."
				: result.Message);
		}
	}

#if OSX
	internal sealed class MacPermissionManager : DefaultPermissionManager
	{
		public override PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null)
		{
			operation ??= "accessibility automation";
			if (MacAccessibility.EnsureAccessibilityAccess(operation, ResolvePrompt(prompt)))
				return new(PermissionStatus.Granted);

			return new(PermissionStatus.Denied,
					$"macOS Accessibility permission is required for '{operation}'. Grant access in System Settings -> Privacy & Security -> Accessibility, then restart the app.");
		}

		public override PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null)
		{
			operation ??= "keyboard/mouse monitoring";
			if (MacAccessibility.EnsureInputMonitoringAccess(operation, ResolvePrompt(prompt)))
				return new(PermissionStatus.Granted);

			return new(PermissionStatus.Denied,
					$"macOS Input Monitoring permission is required for '{operation}'. Grant access in System Settings -> Privacy & Security -> Input Monitoring, then restart the app.");
		}

		public override PermissionResult RequestInputInjection(bool? prompt = null, string operation = null)
		{
			operation ??= "keyboard/mouse sending";
			if (MacAccessibility.EnsurePostEventAccess(operation, ResolvePrompt(prompt)))
				return new(PermissionStatus.Granted);

			return new(PermissionStatus.Denied,
					$"macOS permission is required for '{operation}' to send synthetic keyboard/mouse input. Grant access in System Settings -> Privacy & Security -> Accessibility, then restart the app.");
		}

		public override PermissionResult RequestScreenCapture(bool? prompt = null, string operation = null)
		{
			operation ??= "screen capture";
			if (MacAccessibility.EnsureScreenCaptureAccess(operation, ResolvePrompt(prompt)))
				return new(PermissionStatus.Granted);

			return new(PermissionStatus.Denied,
					$"macOS Screen Recording permission is required for '{operation}'. Grant access in System Settings -> Privacy & Security -> Screen Recording, then restart the app.");
		}

		public override PermissionResult RequestFileAccess(string path, FilePermissionAccess access, bool? prompt = null, string operation = null)
		{
			_ = prompt;
			operation ??= "file access";
			_ = path;
			_ = access;
			// macOS file privacy prompts are generally triggered on direct filesystem access attempts.
			return new(PermissionStatus.NotApplicable, $"'{operation}' uses on-demand OS file permission prompts.");
		}

		// macOS uses separate system dialogs per permission type, so each is requested individually.
		public override PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null)
		{
			PermissionResult result = new(PermissionStatus.NotApplicable);
			if (accessibilityAutomation) result = RequestAccessibilityAutomation(prompt, operation);
			if (monitoring)    result = RequestInputMonitoring(prompt, operation);
			if (injection)     result = RequestInputInjection(prompt, operation);
			if (screenCapture) result = RequestScreenCapture(prompt, operation);
			return result; // blockInput not applicable on macOS
		}
	}
#endif

#if LINUX
	internal sealed class LinuxPermissionManager : DefaultPermissionManager
	{
		public override PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null)
		{
			return UseX11FallbackIfAvailable(
				KeysharpInputdManager.EnsureCapabilities(
					KeysharpInputdClient.Capabilities.AccessibilityAutomation,
					operation ?? "accessibility automation",
					forcePrompt: prompt == true));
		}

		public override PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null)
		{
			var result = KeysharpInputdManager.EnsureCapabilities(
				KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse,
				operation ?? "keyboard/mouse monitoring",
				forcePrompt: prompt == true);

			return UseX11FallbackIfAvailable(result);
		}

		public override PermissionResult RequestInputInjection(bool? prompt = null, string operation = null)
		{
			var result = KeysharpInputdManager.EnsureCapabilities(
				KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse,
				operation ?? "keyboard/mouse sending",
				forcePrompt: prompt == true);

			return UseX11FallbackIfAvailable(result);
		}

		public override PermissionResult RequestScreenCapture(bool? prompt = null, string operation = null)
		{
			// The compositor-specific authorization (KWin/GNOME keysharp-screencap, or NotApplicable) is resolved
			// once inside Platform.Screen — no IsWaylandSession/backend test here.
			return Platform.Screen.RequestCaptureAuthorization(operation ?? "screen capture", prompt == true);
		}

		// Batch all capabilities into a single inputd call so the user sees at most one
		// prompt. Screen capture is included in the inputd request so the combined prompt
		// covers it; inputd writes the PID session grant for all capabilities on ALLOW_ONCE,
		// allowing screencap to skip its own prompt when it checks the session file.
		public override PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null)
		{
			var flags = KeysharpInputdClient.Capabilities.None;

			if (accessibilityAutomation) flags |= KeysharpInputdClient.Capabilities.AccessibilityAutomation;
			if (monitoring)    flags |= KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse;
			if (injection)     flags |= KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse;
			if (blockInput)    flags |= KeysharpInputdClient.Capabilities.BlockInput;
			if (screenCapture) flags |= KeysharpInputdClient.Capabilities.ScreenCapture;

			if (flags == KeysharpInputdClient.Capabilities.None)
				return new(PermissionStatus.NotApplicable);

			return UseX11FallbackIfAvailable(
				KeysharpInputdManager.EnsureCapabilities(flags, operation ?? "RequestCapabilities", forcePrompt: prompt == true));
		}

		private static PermissionResult UseX11FallbackIfAvailable(PermissionResult result)
		{
			if (result.IsGranted)
				return result;

			if (!Platform.Desktop.IsX11Available)
				return result;

			KeysharpInputdManager.ActivateLegacyX11Fallback(result.Message);
			return new PermissionResult(PermissionStatus.NotApplicable, result.Message);
		}
	}
#endif
}
