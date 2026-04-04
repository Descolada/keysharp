using Keysharp.Builtins;
#if LINUX

namespace Keysharp.Internals.Mapper.Linux
{
	/// <summary>
	/// Concrete implementation of Drive for the linux platfrom.
	/// </summary>
	internal class Drive : DriveBase
	{
		internal override long Serial
		{
			get
			{
				if ($"udevadm info --query=property --name={drive.Name} | grep ID_SERIAL_SHORT".Bash(out var serial) != 0)
					return 0L;

				if (!string.IsNullOrEmpty(serial))
				{
					var components = serial.Split('=');

					if (components.Length >= 2)
						return components[1].Al();
				}

				return 0L;
			}
		}

		internal override string StatusCD
		{
			get
			{
				Ks.OutputDebugLine($"Obtaining the status of the CD/DVD drive is not supported on linux.");
				return DefaultObject;
			}
		}

		internal Drive(DriveInfo drv)
			: base(drv) { }

		internal override void Eject()
		{
			if ($"eject {drive.Name}".Bash() != 0)
				Ks.OutputDebugLine($"Drive.Eject failed for {drive.Name}");
		}

		internal override void Lock()
		{
			if ($"eject -i 1 {drive.Name}".Bash() != 0)
				Ks.OutputDebugLine($"Drive.Lock failed for {drive.Name}");
		}

		internal override void Retract()
		{
			if ($"eject -t {drive.Name}".Bash() != 0)
				Ks.OutputDebugLine($"Drive.Retract failed for {drive.Name}");
		}

		internal override void UnLock()
		{
			if ($"eject -i 0 {drive.Name}".Bash() != 0)
				Ks.OutputDebugLine($"Drive.UnLock failed for {drive.Name}");
		}
	}
}
#endif
