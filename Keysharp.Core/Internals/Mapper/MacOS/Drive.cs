using Keysharp.Builtins;
#if OSX

namespace Keysharp.Internals.Mapper.MacOS
{
	/// <summary>
	/// Concrete implementation of Drive for the macOS platform.
	/// </summary>
	internal class Drive : DriveBase
	{
		internal override long Serial
		{
			get
			{
				if ($"diskutil info \"{drive.Name}\" | grep \"Volume UUID\"".Bash(out var output) != 0)
					return 0L;

				if (!string.IsNullOrEmpty(output))
				{
					var components = output.Split(':');

					if (components.Length >= 2)
					{
						var uuid = components[1].Trim().Replace("-", "");

						if (uuid.Length >= 8 && long.TryParse(uuid.Substring(0, 8), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var l))
							return l;
					}
				}

				return 0L;
			}
		}

		internal override string StatusCD
		{
			get
			{
				Ks.OutputDebugLine($"Obtaining the status of the CD/DVD drive is not supported on macOS.");
				return DefaultObject;
			}
		}

		internal Drive(DriveInfo drv)
			: base(drv) { }

		internal override void Eject()
		{
			if ($"diskutil eject \"{drive.Name}\"".Bash() != 0)
				Ks.OutputDebugLine($"Drive.Eject failed for {drive.Name}");
		}

		internal override void Lock()
		{
			Ks.OutputDebugLine($"Locking the eject ability of a drive is not supported on macOS.");
		}

		internal override void Retract()
		{
			if ("drutil tray close".Bash() != 0)
				Ks.OutputDebugLine($"Drive.Retract failed for {drive.Name}");
		}

		internal override void UnLock()
		{
			Ks.OutputDebugLine($"Unlocking the eject ability of a drive is not supported on macOS.");
		}
	}
}
#endif
