using Keysharp.Builtins;
namespace Keysharp.Internals.Mapper
{
	internal abstract class DriveBase
	{
		protected DriveInfo drive;

		internal abstract long Serial { get; }
		internal abstract string StatusCD { get; }

		internal DriveBase(DriveInfo udrive) => drive = udrive;

		/// <summary>
		/// Ejects the CD Drive
		/// </summary>
		internal abstract void Eject();

		internal bool IsCDDrive()
		{
			try
			{
				return drive.DriveType == DriveType.CDRom;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Locks the drives Eject ability
		/// </summary>
		internal abstract void Lock();

		/// <summary>
		/// Retracts the CD Drive
		/// </summary>
		internal abstract void Retract();

		/// <summary>
		/// Changes the volume label.
		/// </summary>
		internal abstract void SetLabel(string label);

		/// <summary>
		/// Unlocks the drives Eject ability
		/// </summary>
		internal abstract void UnLock();

		/// <summary>
		/// Status of the CD
		/// </summary>
		//internal enum StatusCD
		//{
		//  NotReady,
		//  Open,
		//  Playing,
		//  Paused,
		//  Seeking,
		//  Stopped
		//}
	}
}
