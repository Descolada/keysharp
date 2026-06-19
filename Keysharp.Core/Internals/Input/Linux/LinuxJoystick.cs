using Keysharp.Builtins;
#if LINUX
using System.IO;
// Disambiguate from the enclosing Keysharp.Internals.Input.Joystick namespace.
using JoystickCore = Keysharp.Internals.Input.Joystick.Joystick;

namespace Keysharp.Internals.Input.Linux
{
	/// <summary>
	/// evdev-based joystick/gamepad access for Linux, mirroring the winmm joyGetPosEx model used on
	/// Windows. Joysticks are addressed by a zero-based index into the sorted list of
	/// <c>/dev/input/event*</c> devices that expose joystick/gamepad capabilities.
	///
	/// All access is best-effort: if a device cannot be opened (e.g. the user is not in the "input"
	/// group) or an ioctl fails, the joystick is simply treated as absent and the public helpers
	/// return "no joystick" results rather than throwing. This means scripts that query joysticks on
	/// a machine without one (the common case) behave gracefully instead of crashing.
	///
	/// The axis mapping from Windows' fixed X/Y/Z/R/U/V set onto evdev axes is necessarily a
	/// heuristic, because evdev exposes device-specific axis codes. The chosen mapping matches the
	/// most common convention used by SDL/Wine for the legacy joystick API.
	/// </summary>
	internal static class LinuxJoystick
	{
		// --- libc -------------------------------------------------------------------------------
		private const int O_RDONLY = 0;
		private const int O_NONBLOCK = 0x800;

		[DllImport("libc", SetLastError = true)]
		private static extern int open(string pathname, int flags);

		[DllImport("libc", SetLastError = true)]
		private static extern int close(int fd);

		[DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
		private static extern int ioctl(int fd, nuint request, byte[] buf);

		[DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
		private static extern int ioctl(int fd, nuint request, ref InputAbsInfo info);

		// --- evdev ioctl encoding (linux/input.h, asm-generic/ioctl.h) --------------------------
		private const int IOC_TYPESHIFT = 8;
		private const int IOC_SIZESHIFT = 16;
		private const int IOC_DIRSHIFT = 30;
		private const int IOC_READ = 2;
		private const int EVDEV_TYPE = 'E';

		private static nuint EVIOCGNAME(int len) => Encode(0x06, len);
		private static nuint EVIOCGKEY(int len) => Encode(0x18, len);
		private static nuint EVIOCGBIT(int ev, int len) => Encode(0x20 + ev, len);
		private static nuint EVIOCGABS(int abs) => Encode(0x40 + abs, Marshal.SizeOf<InputAbsInfo>());

		private static nuint Encode(int nr, int size) =>
			(nuint)(((uint)IOC_READ << IOC_DIRSHIFT) | ((uint)size << IOC_SIZESHIFT) | ((uint)EVDEV_TYPE << IOC_TYPESHIFT) | (uint)nr);

		// --- evdev codes (linux/input-event-codes.h) -------------------------------------------
		private const int EV_KEY = 0x01;
		private const int EV_ABS = 0x03;
		private const int EV_MAX = 0x1f;

		private const int ABS_X = 0x00;
		private const int ABS_Y = 0x01;
		private const int ABS_Z = 0x02;
		private const int ABS_RX = 0x03;
		private const int ABS_RY = 0x04;
		private const int ABS_RZ = 0x05;
		private const int ABS_HAT0X = 0x10;
		private const int ABS_HAT0Y = 0x11;
		private const int ABS_MAX = 0x3f;

		private const int BTN_MISC = 0x100;
		private const int BTN_JOYSTICK = 0x120;
		private const int KEY_MAX = 0x2ff;

		[StructLayout(LayoutKind.Sequential)]
		private struct InputAbsInfo
		{
			public int value;
			public int minimum;
			public int maximum;
			public int fuzz;
			public int flat;
			public int resolution;
		}

		// Windows axis -> evdev axis. R is "rudder or 4th axis"; the rest follow the common
		// SDL/Wine convention for the legacy joystick API.
		private static int AbsForJoyControl(JoyControls joy) => joy switch
		{
			JoyControls.Xpos => ABS_X,
			JoyControls.Ypos => ABS_Y,
			JoyControls.Zpos => ABS_Z,
			JoyControls.Rpos => ABS_RZ,
			JoyControls.Upos => ABS_RX,
			JoyControls.Vpos => ABS_RY,
			_ => -1
		};

		private static bool TestBit(byte[] bits, int bit) => bit >= 0 && (bit >> 3) < bits.Length && (bits[bit >> 3] & (1 << (bit & 7))) != 0;

		// Enumerating every /dev/input/event* device (open + ioctl + close each) is comparatively
		// expensive, and GetKeyState("Joy…") can be polled in a tight loop. Cache the device-path list
		// briefly so a poll loop reuses it, while still picking up a newly plugged controller within the TTL.
		private static readonly Lock deviceCacheGate = new();
		private static List<string> cachedDevices;
		private static long cacheStampTicks;
		private const long CacheTtlTicks = TimeSpan.TicksPerSecond; // re-scan at most ~once per second

		private static List<string> GetDevices()
		{
			lock (deviceCacheGate)
			{
				var now = DateTime.UtcNow.Ticks;

				if (cachedDevices == null || now - cacheStampTicks > CacheTtlTicks)
				{
					cachedDevices = EnumerateDevices();
					cacheStampTicks = now;
				}

				return cachedDevices;
			}
		}

		/// <summary>
		/// Returns the device paths of all joystick/gamepad-like input devices, ordered by event index.
		/// </summary>
		private static List<string> EnumerateDevices()
		{
			var found = new List<(int idx, string path)>();

			try
			{
				foreach (var path in Directory.EnumerateFiles("/dev/input", "event*"))
				{
					var name = Path.GetFileName(path);

					if (!int.TryParse(name.AsSpan("event".Length), out var idx))
						continue;

					var fd = open(path, O_RDONLY | O_NONBLOCK);

					if (fd < 0)
						continue;

					try
					{
						if (IsJoystick(fd))
							found.Add((idx, path));
					}
					finally
					{
						_ = close(fd);
					}
				}
			}
			catch
			{
				// /dev/input missing or unreadable: treat as "no joysticks".
			}

			found.Sort((a, b) => a.idx.CompareTo(b.idx));
			return found.ConvertAll(f => f.path);
		}

		private static bool IsJoystick(int fd)
		{
			var evbit = new byte[(EV_MAX >> 3) + 1];

			if (ioctl(fd, EVIOCGBIT(0, evbit.Length), evbit) < 0 || !TestBit(evbit, EV_ABS) || !TestBit(evbit, EV_KEY))
				return false;

			var absbit = new byte[(ABS_MAX >> 3) + 1];

			if (ioctl(fd, EVIOCGBIT(EV_ABS, absbit.Length), absbit) < 0 || !TestBit(absbit, ABS_X))
				return false;

			var keybit = new byte[(KEY_MAX >> 3) + 1];

			if (ioctl(fd, EVIOCGBIT(EV_KEY, keybit.Length), keybit) < 0)
				return false;

			// A real joystick/gamepad exposes at least one button in the joystick/gamepad range.
			for (var b = BTN_JOYSTICK; b < BTN_JOYSTICK + 0x20; ++b)
				if (TestBit(keybit, b))
					return true;

			return false;
		}

		/// <summary>
		/// Opens the joystick at the given zero-based index, or returns -1 if it does not exist.
		/// </summary>
		private static int OpenByIndex(uint index)
		{
			var devices = GetDevices();
			return index < devices.Count ? open(devices[(int)index], O_RDONLY | O_NONBLOCK) : -1;
		}

		/// <summary>
		/// Builds the ordered list of supported button codes, mirroring the kernel joydev driver:
		/// joystick/gamepad buttons first (BTN_JOYSTICK..KEY_MAX), then the misc buttons
		/// (BTN_MISC..BTN_JOYSTICK). The list index + 1 is the script-visible button number.
		/// </summary>
		private static List<int> GetButtonCodes(int fd)
		{
			var codes = new List<int>();
			var keybit = new byte[(KEY_MAX >> 3) + 1];

			if (ioctl(fd, EVIOCGBIT(EV_KEY, keybit.Length), keybit) < 0)
				return codes;

			for (var b = BTN_JOYSTICK; b <= KEY_MAX; ++b)
				if (TestBit(keybit, b))
					codes.Add(b);

			for (var b = BTN_MISC; b < BTN_JOYSTICK; ++b)
				if (TestBit(keybit, b))
					codes.Add(b);

			return codes;
		}

		/// <summary>
		/// Returns the currently-pressed button bitmask (bit i set => button i+1 down), limited to
		/// JoystickData.MaxJoyButtons. Returns false if the device state cannot be read.
		/// </summary>
		private static bool TryGetButtons(int fd, out uint buttons)
		{
			buttons = 0;
			var codes = GetButtonCodes(fd);

			if (codes.Count == 0)
				return false;

			var keystate = new byte[(KEY_MAX >> 3) + 1];

			if (ioctl(fd, EVIOCGKEY(keystate.Length), keystate) < 0)
				return false;

			var count = Math.Min(codes.Count, JoystickData.MaxJoyButtons);

			for (var i = 0; i < count; ++i)
				if (TestBit(keystate, codes[i]))
					buttons |= 1u << i;

			return true;
		}

		private static bool TryGetAbs(int fd, int abs, out InputAbsInfo info)
		{
			info = default;
			return ioctl(fd, EVIOCGABS(abs), ref info) >= 0;
		}

		/// <summary>
		/// Polls every joystick that currently has hotkeys bound and queues the hotkey messages for
		/// any buttons that have newly transitioned to the down state, mirroring the Windows path.
		/// </summary>
		internal static void PollJoysticks()
		{
			var script = Script.TheScript;
			var jd = script.JoystickData;
			var devices = GetDevices();

			for (var i = 0; i < JoystickData.MaxJoysticks; ++i)
			{
				if (!script.HotkeyData.joystickHasHotkeys[i])
					continue;

				if (i >= devices.Count)
					continue;

				var fd = open(devices[i], O_RDONLY | O_NONBLOCK);

				if (fd < 0)
					continue;

				uint buttons;

				try
				{
					if (!TryGetButtons(fd, out buttons))
						continue;
				}
				finally
				{
					_ = close(fd);
				}

				// XOR finds changed buttons; AND with the current state keeps only up->down transitions.
				var buttonsNewlyDown = (buttons ^ jd.buttonsPrev[i]) & buttons;
				jd.buttonsPrev[i] = buttons;

				if (buttonsNewlyDown != 0)
					HotkeyDefinition.TriggerJoyHotkeys(i, buttonsNewlyDown);
			}
		}

		/// <summary>
		/// Returns the requested joystick state, mirroring the Windows ScriptGetJoyState contract:
		/// a bool for buttons, a double percentage for axes, a long for POV, and strings for the
		/// informational queries. Returns false / blank when the joystick or control is unavailable.
		/// </summary>
		internal static object ScriptGetJoyState(JoyControls joy, uint joystickID)
		{
			var fd = OpenByIndex(joystickID);

			if (fd < 0)
				return false;

			try
			{
				if (JoystickCore.IsJoystickButton(joy))
				{
					if (!TryGetButtons(fd, out var buttons))
						return false;

					var bit = (int)joy - (int)JoyControls.Button1;
					return bit >= 0 && bit < JoystickData.MaxJoyButtons && ((buttons >> bit) & 0x1) != 0;
				}

				switch (joy)
				{
					case JoyControls.Xpos:
					case JoyControls.Ypos:
					case JoyControls.Zpos:
					case JoyControls.Rpos:
					case JoyControls.Upos:
					case JoyControls.Vpos:
					{
						var abs = AbsForJoyControl(joy);

						if (abs < 0 || !TryGetAbs(fd, abs, out var info))
							return 0.0;

						var range = info.maximum - info.minimum;
						return range != 0 ? 100.0 * (info.value - info.minimum) / range : (double)info.value;
					}

					case JoyControls.Pov:
						return GetPov(fd);

					case JoyControls.Name:
						return GetName(fd);

					case JoyControls.Buttons:
						return (long)GetButtonCodes(fd).Count;

					case JoyControls.Axes:
						return (long)CountAxes(fd);

					case JoyControls.Info:
						return GetInfo(fd);
				}
			}
			finally
			{
				_ = close(fd);
			}

			return false;
		}

		private static long GetPov(int fd)
		{
			if (!TryGetAbs(fd, ABS_HAT0X, out var hx) || !TryGetAbs(fd, ABS_HAT0Y, out var hy))
				return -1L;

			var x = Math.Sign(hx.value);
			var y = Math.Sign(hy.value);

			// Centidegrees, clockwise from North, matching Windows JOYINFOEX.dwPOV. -1 when centered.
			return (x, y) switch
			{
				(0, -1) => 0L,
				(1, -1) => 4500L,
				(1, 0) => 9000L,
				(1, 1) => 13500L,
				(0, 1) => 18000L,
				(-1, 1) => 22500L,
				(-1, 0) => 27000L,
				(-1, -1) => 31500L,
				_ => -1L
			};
		}

		private static int CountAxes(int fd)
		{
			var count = 0;

			foreach (var joy in new[] { JoyControls.Xpos, JoyControls.Ypos, JoyControls.Zpos, JoyControls.Rpos, JoyControls.Upos, JoyControls.Vpos })
				if (TryGetAbs(fd, AbsForJoyControl(joy), out _))
					count++;

			return count;
		}

		private static string GetInfo(int fd)
		{
			var str = "";

			if (TryGetAbs(fd, ABS_Z, out _)) str += 'Z';
			if (TryGetAbs(fd, ABS_RZ, out _)) str += 'R';
			if (TryGetAbs(fd, ABS_RX, out _)) str += 'U';
			if (TryGetAbs(fd, ABS_RY, out _)) str += 'V';

			if (TryGetAbs(fd, ABS_HAT0X, out _))
			{
				str += 'P';
				str += 'D'; // evdev hat0 reports discrete 4/8-way directions.
			}

			return str;
		}

		private static string GetName(int fd)
		{
			var buf = new byte[128];

			if (ioctl(fd, EVIOCGNAME(buf.Length), buf) < 0)
				return "";

			var len = System.Array.IndexOf(buf, (byte)0);
			return System.Text.Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
		}
	}
}
#endif
