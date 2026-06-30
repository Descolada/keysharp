using Keysharp.Builtins;

namespace Keysharp.Internals
{
#if WINDOWS
	internal sealed class WindowsSession : ISession
	{
		public bool ExitProgram(uint flags, uint reason) => Keysharp.Internals.Os.Windows.WindowsAPI.ExitWindowsEx(flags, reason);
	}
#elif LINUX
	/// <summary>
	/// Linux session control. Logoff is per-DE (the desktop-environment flags are detected once at startup);
	/// shutdown/reboot use the standard coreutils commands. Taken from the per-DE logout reference.
	/// </summary>
	internal sealed class LinuxSession : ISession
	{
		public bool ExitProgram(uint flags, uint reason)
		{
			var cmd = "";
			var force = (flags & 4) == 4;   // close all programs

			if (flags == 0)   // Logoff
			{
				if (IsGnome)
					cmd = force ? "gnome-session-quit --force" : "gnome-session-quit";
				else if (IsKde)
					cmd = force ? "qdbus org.kde.ksmserver /KSMServer logout 0 0 2" : "qdbus org.kde.ksmserver /KSMServer logout 1 0 3";
				else if (IsXfce)
					cmd = force ? "xfce4-session-logout --fast" : "xfce4-session-logout";
				else if (IsMate)
					cmd = force ? "mate-session-save --logout --force" : "mate-session-save --logout";
				else if (IsCinnamon)
					cmd = force ? "cinnamon-session-quit --no-prompt" : "cinnamon-session-quit";
				else if (IsLxqt)
				{
					if (force)
						Ks.OutputDebugLine("LXQT doesn't support forced logouts.");

					cmd = "lxqt-leave";
				}
				else if (IsLxde)
				{
					if (force)
						Ks.OutputDebugLine("LXDE doesn't support forced logouts.");

					cmd = "lxde-logout";
				}

				if (!string.IsNullOrWhiteSpace(cmd) && cmd.Bash() != 0)
					Ks.OutputDebugLine($"ExitProgram logoff command failed: {cmd}");
			}
			else if ((flags & 1) == 1)   // Halt/shutdown
			{
				if ((flags & 8) == 8)   // Power down
				{
					if ("shutdown now".Bash() != 0)
						Ks.OutputDebugLine("ExitProgram shutdown command failed: shutdown now");
				}
				else
				{
					if ("halt".Bash() != 0)
						Ks.OutputDebugLine("ExitProgram halt command failed: halt");
				}
			}
			else if ((flags & 2) == 2)   // Reboot
			{
				if (force)
				{
					if ("reboot -f".Bash() != 0)
						Ks.OutputDebugLine("ExitProgram reboot command failed: reboot -f");
				}
				else
				{
					if ("reboot".Bash() != 0)
						Ks.OutputDebugLine("ExitProgram reboot command failed: reboot");
				}
			}
			else if ((flags & 8) == 8)   // Shutdown
			{
				if ("shutdown now".Bash() != 0)
					Ks.OutputDebugLine("ExitProgram shutdown command failed: shutdown now");
			}

			return true;
		}
	}
#elif OSX
	internal sealed class MacSession : ISession
	{
		public bool ExitProgram(uint flags, uint reason)
		{
			Ks.OutputDebugLine("ExitProgram is not implemented for macOS yet.");
			return false;
		}
	}
#endif
}
