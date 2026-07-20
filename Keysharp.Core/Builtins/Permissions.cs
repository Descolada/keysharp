namespace Keysharp.Builtins
{
	/// <summary>
	/// Public interface for requesting platform permissions ahead of use.
	/// </summary>
	public partial class Ks
	{
		private enum KeysharpCapability
		{
			AccessibilityAutomation,
			BlockInput,
			InputInjection,
			InputMonitoring,
			ScreenCapture,
			Interception
		}

		/// <summary>
		/// Requests one or more platform capabilities, batching where possible to minimise the number
		/// of prompts shown to the user, then returns the current status of every capability.
		/// When called with no arguments the current status of all capabilities is returned without prompting.
		/// </summary>
		/// <param name="capabilities">
		/// Zero or more capability names (strings or Arrays of strings). Names may be comma/space-delimited.
		/// Recognised aliases: "accessibility", "blockinput", "inputinjection"/"synthinput",
		/// "inputmonitoring"/"hook", "screencapture"/"capture".
		/// </param>
		/// <returns>
		/// An Object with a property per capability ("Granted"|"Denied"|"NotApplicable"|"Unsupported")
		/// and a <c>Granted</c> property (1/0) that is true only when every <em>requested</em> capability
		/// was granted or not applicable.
		/// </returns>
		public static KeysharpObject RequestCapabilities(params object[] capabilities)
		{
			List<KeysharpCapability> requested = null;

			if (capabilities.Length > 0)
			{
				requested = ParseRequestedCapabilities(capabilities);
				RequestCapabilitiesBatched(requested);
			}

			var result = new KeysharpObject();
			var allGranted = true;

			foreach (KeysharpCapability cap in Enum.GetValues<KeysharpCapability>())
			{
				var permission = QueryCapabilityStatus(cap);
				result.DefinePropInternal(CapabilityName(cap), new OwnPropsDesc(result, permission.Status.ToString()));

				if (requested == null || requested.Contains(cap))
					allGranted &= permission.IsGranted;
			}

			result.DefinePropInternal("Granted", new OwnPropsDesc(result, allGranted ? 1L : 0L));
			return result;
		}

		private static void RequestCapabilitiesBatched(List<KeysharpCapability> requested)
		{
			var permissions = Script.TheScript.Permissions;

			var monitoring    = requested.Contains(KeysharpCapability.InputMonitoring);
			var injection     = requested.Contains(KeysharpCapability.InputInjection);
			var blockInput    = requested.Contains(KeysharpCapability.BlockInput);
			var screenCapture = requested.Contains(KeysharpCapability.ScreenCapture);
			var accessibility = requested.Contains(KeysharpCapability.AccessibilityAutomation);

			// Input capabilities (hooks/synth/block) are one enforcement domain and screen
			// capture is a SEPARATE one, each with its own prompt — like macOS's separate
			// Accessibility and Screen Recording permissions. Request each exactly once
			// (screenCapture:false here so it is not also asked for inside the input call).
			if (monitoring || injection || blockInput || accessibility)
				permissions.RequestInputCapabilities(monitoring, injection, blockInput, screenCapture: false, accessibility, prompt: true, operation: "RequestCapabilities");

			if (screenCapture)
				permissions.RequestScreenCapture(prompt: true, operation: "RequestCapabilities");
		}

		/// <summary>
		/// Backing method for the <c>#Requires capability</c> directive: requests the listed
		/// capabilities up front and EXITS the app if any is denied — the script declared it
		/// cannot run without them. The runtime <see cref="RequestCapabilities"/> builtin
		/// deliberately does NOT exit; it returns status for the script to handle instead.
		/// </summary>
		public static object RequireCapabilities(params object[] capabilities)
		{
			var requested = ParseRequestedCapabilities(capabilities);
			RequestCapabilitiesBatched(requested);

			var denied = new List<string>();

			foreach (var cap in requested)
				if (!QueryCapabilityStatus(cap).IsGranted)
					denied.Add(CapabilityName(cap));

			if (denied.Count == 0)
				return DefaultObject;

			_ = OutputDebugLine(
				$"Keysharp: required capability/capabilities not granted: {string.Join(", ", denied)}. Exiting. " +
				"Re-run and choose Allow (or grant it persistently) to continue.");
			return Flow.ExitApp(1L);
		}

		private static PermissionResult QueryCapabilityStatus(KeysharpCapability capability)
		{
			var permissions = Script.TheScript.Permissions;

			return capability switch
				{
					KeysharpCapability.AccessibilityAutomation
						=> permissions.RequestAccessibilityAutomation(prompt: false),
					KeysharpCapability.InputInjection
						=> permissions.RequestInputInjection(prompt: false),
					KeysharpCapability.InputMonitoring
						=> permissions.RequestInputMonitoring(prompt: false),
					KeysharpCapability.ScreenCapture
						=> permissions.RequestScreenCapture(prompt: false),
					KeysharpCapability.BlockInput
						=> QueryBlockInputCapability(),
					KeysharpCapability.Interception
						=> permissions.RequestInterceptionDriver(prompt: false),
				_ => new PermissionResult(PermissionStatus.Unsupported)
			};
		}

		private static PermissionResult QueryBlockInputCapability()
		{
#if LINUX
				// Status query — peek, never prompt.
				return Keysharp.Internals.Input.Linux.KeysharpInputdManager.PeekInputCapability(
					Keysharp.Internals.Input.Linux.KeysharpInputdClient.Capabilities.BlockInput);
#else
			return new PermissionResult(PermissionStatus.NotApplicable);
#endif
		}

		private static List<KeysharpCapability> ParseRequestedCapabilities(object[] capabilities)
		{
			var requested = new List<KeysharpCapability>();

			foreach (var cap in capabilities)
			{
				if (cap is Array arr)
				{
					foreach (var item in arr)
						AddRequestedCapability(requested, item.As());
				}
				else
				{
					foreach (var part in cap.As().Split([' ', '\t', '\r', '\n', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
						AddRequestedCapability(requested, part);
				}
			}

			if (requested.Count == 0)
				throw new ValueError("At least one capability name is required.");

			return requested;
		}

		private static void AddRequestedCapability(List<KeysharpCapability> requested, string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return;

			var capability = ParseCapabilityName(name);

			if (!requested.Contains(capability))
				requested.Add(capability);
		}

		private static KeysharpCapability ParseCapabilityName(string name)
		{
			var normalized = NormalizeCapabilityName(name);

			return normalized switch
			{
				"accessibility" or "accessibilityautomation" or "automation" or "windowautomation"
					=> KeysharpCapability.AccessibilityAutomation,
				"blockinput" or "inputblock" or "inputblocking"
					=> KeysharpCapability.BlockInput,
				"inputinjection" or "inputsending" or "inputsend" or "sendinput" or "synthinput" or "synthesizeinput"
					=> KeysharpCapability.InputInjection,
				"inputmonitoring" or "inputhook" or "inputhooks" or "hookinput" or "keyboardmousemonitoring"
					=> KeysharpCapability.InputMonitoring,
				"screencapture" or "screenrecording" or "capture" or "imagecapture"
					=> KeysharpCapability.ScreenCapture,
				"interception" or "interceptiondriver"
					=> KeysharpCapability.Interception,
				_ => throw new ValueError($"Unknown capability name: {name}.")
			};
		}

		private static string NormalizeCapabilityName(string name)
		{
			var builder = new StringBuilder(name.Length);

			foreach (var ch in name)
			{
				if (char.IsLetterOrDigit(ch))
					builder.Append(char.ToLowerInvariant(ch));
			}

			return builder.ToString();
		}

		private static string CapabilityName(KeysharpCapability capability)
			=> capability switch
			{
				KeysharpCapability.AccessibilityAutomation => "AccessibilityAutomation",
				KeysharpCapability.BlockInput => "BlockInput",
				KeysharpCapability.InputInjection => "InputInjection",
				KeysharpCapability.InputMonitoring => "InputMonitoring",
				KeysharpCapability.ScreenCapture => "ScreenCapture",
				KeysharpCapability.Interception => "Interception",
				_ => capability.ToString()
			};
	}
}
