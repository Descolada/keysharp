namespace Keysharp.Core.Common.Platform
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
		protected static bool ResolvePrompt(bool? prompt) => prompt ?? !Script.IsTestHost;

		public virtual PermissionResult RequestAccessibilityAutomation(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
		public virtual PermissionResult RequestInputMonitoring(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
		public virtual PermissionResult RequestInputInjection(bool? prompt = null, string operation = null) => new(PermissionStatus.NotApplicable);
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
	}
#endif
}
