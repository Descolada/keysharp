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
			// Aggregate to the WORST (least-granted) status across every requested capability rather than
			// whichever happens to be checked last, so a denial of one capability is never masked by a later
			// Granted/NotApplicable. blockInput carries no per-capability status of its own here.
			var result = new PermissionResult(PermissionStatus.NotApplicable);
			if (accessibilityAutomation) result = Combine(result, RequestAccessibilityAutomation(prompt, operation));
			if (monitoring)    result = Combine(result, RequestInputMonitoring(prompt, operation));
			if (injection)     result = Combine(result, RequestInputInjection(prompt, operation));
			if (screenCapture) result = Combine(result, RequestScreenCapture(prompt, operation));
			return result;
		}

		// Keeps the first NON-granted result (its status + message); a granted/NotApplicable result never
		// overrides an earlier denial, so a batched request reports success only when every part succeeded.
		protected static PermissionResult Combine(PermissionResult accumulated, PermissionResult next)
			=> accumulated.IsGranted ? next : accumulated;
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

		// macOS uses separate system dialogs per permission type, so each is requested individually. Aggregate to
		// the worst-of (see DefaultPermissionManager.Combine) so an earlier denial isn't masked by a later grant.
		public override PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null)
		{
			var result = new PermissionResult(PermissionStatus.NotApplicable);
			if (accessibilityAutomation) result = Combine(result, RequestAccessibilityAutomation(prompt, operation));
			if (monitoring)    result = Combine(result, RequestInputMonitoring(prompt, operation));
			if (injection)     result = Combine(result, RequestInputInjection(prompt, operation));
			if (screenCapture) result = Combine(result, RequestScreenCapture(prompt, operation));
			return result; // blockInput not applicable on macOS
		}
	}
#endif

#if LINUX
	internal sealed class LinuxPermissionManager : DefaultPermissionManager
	{
		public override PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null)
		{
			if (Script.IsHeadless)
				return new PermissionResult(PermissionStatus.NotApplicable);

			var result = KeysharpInputdManager.EnsureCapabilities(
				KeysharpInputdClient.Capabilities.AccessibilityAutomation,
				operation ?? "accessibility automation",
				forcePrompt: prompt == true);

			return result.IsGranted ? result : new PermissionResult(PermissionStatus.NotApplicable, result.Message);
		}

		public override PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null)
		{
			var caps = KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse;

			// prompt:false is a status query — peek, never prompt.
			if (prompt == false)
				return KeysharpInputdManager.PeekInputCapability(caps);

			return KeysharpInputdManager.EnsureCapabilities(caps, operation ?? "keyboard/mouse monitoring", forcePrompt: prompt == true);
		}

		public override PermissionResult RequestInputInjection(bool? prompt = null, string operation = null)
		{
			var caps = KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse;

			if (prompt == false)
				return KeysharpInputdManager.PeekInputCapability(caps);

			return KeysharpInputdManager.EnsureCapabilities(caps, operation ?? "keyboard/mouse sending", forcePrompt: prompt == true);
		}

		public override PermissionResult RequestScreenCapture(bool? prompt = null, string operation = null)
		{
			// The compositor-specific authorization (KWin/GNOME keysharp-helper, or NotApplicable) is resolved
			// once inside Platform.Screen — no IsWaylandSession/backend test here.
			return Platform.Screen.RequestCaptureAuthorization(operation ?? "screen capture", prompt == true);
		}

		// Input capabilities (hooks/synth/block) are one enforcement domain (keysharp-inputd)
		// and screen capture is a SEPARATE one (keysharp-helper) with its own prompt — like
		// macOS's separate Accessibility and Screen Recording permissions. So they are
		// requested independently: the inputd request coalesces to the full input set
		// (ExpandInputPermissionRequest) for a single input prompt, and screen capture
		// prompts on its own. Aggregate to the worst status (Combine).
		public override PermissionResult RequestInputCapabilities(bool monitoring, bool injection, bool blockInput, bool screenCapture = false, bool accessibilityAutomation = false, bool? prompt = null, string operation = null)
		{
			var flags = KeysharpInputdClient.Capabilities.None;

			if (accessibilityAutomation) flags |= KeysharpInputdClient.Capabilities.AccessibilityAutomation;
			if (monitoring)    flags |= KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse;
			if (injection)     flags |= KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse;
			if (blockInput)    flags |= KeysharpInputdClient.Capabilities.BlockInput;

			var result = new PermissionResult(PermissionStatus.NotApplicable);

			if (flags != KeysharpInputdClient.Capabilities.None)
				result = Combine(result, KeysharpInputdManager.EnsureCapabilities(flags, operation ?? "RequestCapabilities", forcePrompt: prompt == true));

			if (screenCapture)
				result = Combine(result, RequestScreenCapture(prompt, operation));

			return result;
		}
	}
#endif
}
