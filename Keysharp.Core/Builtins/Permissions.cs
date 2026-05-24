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
		/// Requests one or more Keysharp platform capabilities, optionally returning per-capability details.
		/// </summary>
		/// <param name="capabilities">Capability name, delimited capability names, or an array of capability names.</param>
		/// <param name="result">Optional output variable receiving a Map with Status, Granted, Denied, NotApplicable, Unsupported, and Details.</param>
		/// <returns>1 if every requested capability was granted or not applicable, otherwise 0.</returns>
		public static long RequestCapabilities(object capabilities, [ByRef] object result = null)
		{
			var requested = ParseRequestedCapabilities(capabilities);
			var granted = new Array();
			var denied = new Array();
			var notApplicable = new Array();
			var unsupported = new Array();
			var details = new Map();
			var messages = new List<string>();
			var allGranted = true;

			foreach (var capability in requested)
			{
				var permission = RequestCapability(capability);
				var capabilityName = CapabilityName(capability);
				var detail = new Map(
					"Status", permission.Status.ToString(),
					"Message", permission.Message);

				details[capabilityName] = detail;

				if (!permission.Message.IsNullOrEmpty())
					messages.Add($"{capabilityName}: {permission.Message}");

				switch (permission.Status)
				{
					case PermissionStatus.Granted:
						granted.Push(capabilityName);
						break;

					case PermissionStatus.NotApplicable:
						notApplicable.Push(capabilityName);
						break;

					case PermissionStatus.Unsupported:
						unsupported.Push(capabilityName);
						allGranted = false;
						break;

					default:
						denied.Push(capabilityName);
						allGranted = false;
						break;
				}
			}

			if (result != null)
			{
				var status = allGranted
					? "Granted"
					: granted.Count > 0 || notApplicable.Count > 0 ? "Partial"
					: unsupported.Count > 0 && denied.Count == 0 ? "Unsupported"
					: "Denied";
				Script.SetPropertyValue(result, "__Value", new Map(
					"Status", status,
					"Granted", granted,
					"Denied", denied,
					"NotApplicable", notApplicable,
					"Unsupported", unsupported,
					"Message", string.Join(Environment.NewLine, messages),
					"Details", details));
			}

			return allGranted ? 1L : 0L;
		}

		private static PermissionResult RequestCapability(KeysharpCapability capability)
		{
			var permissions = Script.TheScript.Permissions;

			return capability switch
			{
				KeysharpCapability.AccessibilityAutomation
					=> permissions.RequestAccessibilityAutomation(operation: "RequestCapabilities"),
				KeysharpCapability.InputInjection
					=> permissions.RequestInputInjection(operation: "RequestCapabilities"),
				KeysharpCapability.InputMonitoring
					=> permissions.RequestInputMonitoring(operation: "RequestCapabilities"),
				KeysharpCapability.ScreenCapture
					=> permissions.RequestScreenCapture(operation: "RequestCapabilities"),
				KeysharpCapability.BlockInput
					=> RequestBlockInputCapability(),
				_ => new PermissionResult(PermissionStatus.Unsupported)
			};
		}

		private static PermissionResult RequestBlockInputCapability()
		{
#if LINUX
			return Keysharp.Internals.Input.Linux.KeysharpInputdManager.EnsureCapabilities(
				Keysharp.Internals.Input.Linux.KeysharpInputdClient.Capabilities.BlockInput,
				"RequestCapabilities");
#else
			return new PermissionResult(PermissionStatus.NotApplicable);
#endif
		}

		private static List<KeysharpCapability> ParseRequestedCapabilities(object capabilities)
		{
			var requested = new List<KeysharpCapability>();

			if (capabilities is Array array)
			{
				foreach (var item in array)
					AddRequestedCapability(requested, item.As());
			}
			else
			{
				var text = capabilities.As();

				foreach (var part in text.Split([' ', '\t', '\r', '\n', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
					AddRequestedCapability(requested, part);
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
