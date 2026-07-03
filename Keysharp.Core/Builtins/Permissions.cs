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
			ScreenCapture
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

			// When input capabilities are requested, route screen capture through the
			// same inputd call.  On Linux this produces one combined prompt for all
			// capabilities; inputd writes the PID session so that keysharp-helper finds it
			// and skips its own prompt.  When no input caps are requested, screen capture
			// falls through to RequestScreenCapture below.
			if (monitoring || injection || blockInput || accessibility)
				permissions.RequestInputCapabilities(monitoring, injection, blockInput, screenCapture, accessibility, prompt: true, operation: "RequestCapabilities");

			// Always call RequestScreenCapture — on Linux keysharp-helper checks the PID
			// session (possibly written by inputd above) and skips prompting if already
			// granted.  On non-Linux platforms this shows the platform's own dialog.
			if (screenCapture)
				permissions.RequestScreenCapture(prompt: true, operation: "RequestCapabilities");

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
				_ => new PermissionResult(PermissionStatus.Unsupported)
			};
		}

		private static PermissionResult QueryBlockInputCapability()
		{
#if LINUX
				return Keysharp.Internals.Input.Linux.KeysharpInputdManager.EnsureCapabilities(
					Keysharp.Internals.Input.Linux.KeysharpInputdClient.Capabilities.BlockInput,
					"QueryCapabilities",
					forcePrompt: false);
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
				_ => capability.ToString()
			};
	}
}
