using Keysharp.Builtins;
#if !WINDOWS

using VirtualKeys = Keysharp.Internals.Input.Keyboard.VirtualKeys;
#if OSX
using MonoMac.AppKit;
#endif

namespace Keysharp.Internals.Platform.Unix
{
	/// <summary>
	/// Concrete implementation of PlatformManager for the linux platfrom.
	/// </summary>
	internal partial class PlatformManager : PlatformManagerBase, IPlatformManager
	{
		private static readonly bool isGnome, isKde, isXfce, isMate, isCinnamon, isLxqt, isLxde;

		internal static bool IsGnome => isGnome;
		internal static bool IsKde => isKde;
		internal static bool IsXfce => isXfce;
		internal static bool IsMate => isMate;
		internal static bool IsCinnamon => isCinnamon;
		internal static bool IsLxqt => isLxqt;
		internal static bool IsLxde => isLxde;

		internal static bool IsX11Available =>
#if OSX
			false;
#else
			XDisplay.Default.Handle != 0;
#endif

		internal static readonly bool IsWaylandSession =
			string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

		static PlatformManager()
		{
#if LINUX
			// Detect the desktop environment from the standard freedesktop variables rather than
			// shelling out to bash on every startup. XDG_CURRENT_DESKTOP is the canonical source (a
			// colon-separated list, e.g. "ubuntu:GNOME"); fall back to the older session variables.
			// These flags only select the per-DE logout/session command, so an unrecognized DE leaves
			// them all false (no command is sent) instead of wrongly assuming GNOME.
			var de = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
					  ?? Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP")
					  ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
					  ?? string.Empty).ToLowerInvariant();

			if (de.Contains("gnome") || de.Contains("unity"))
				isGnome = true;
			else if (de.Contains("kde") || de.Contains("plasma"))
				isKde = true;
			else if (de.Contains("xfce"))
				isXfce = true;
			else if (de.Contains("mate"))
				isMate = true;
			else if (de.Contains("cinnamon"))
				isCinnamon = true;
			else if (de.Contains("lxqt"))
				isLxqt = true;
			else if (de.Contains("lxde"))
				isLxde = true;
#endif
		}

		// Return the current xkb_keymap pointer as a stand-in for HKL.
		public static nint GetKeyboardLayout(uint idThread)
		{
			return KeyCodes.GetCurrentKeymapHandle();
		}

		private static void DeriveModifierState(byte[] lpKeyState, out bool shift, out bool caps, out bool altGr)
		{
			shift =
				(lpKeyState.Length > VirtualKeys.VK_SHIFT && (lpKeyState[VirtualKeys.VK_SHIFT] & 0x80) != 0) ||
				(lpKeyState.Length > VirtualKeys.VK_LSHIFT && (lpKeyState[VirtualKeys.VK_LSHIFT] & 0x80) != 0) ||
				(lpKeyState.Length > VirtualKeys.VK_RSHIFT && (lpKeyState[VirtualKeys.VK_RSHIFT] & 0x80) != 0);

			caps = lpKeyState.Length > VirtualKeys.VK_CAPITAL && (lpKeyState[VirtualKeys.VK_CAPITAL] & 0x01) != 0;

			altGr =
				(lpKeyState.Length > VirtualKeys.VK_RMENU && (lpKeyState[VirtualKeys.VK_RMENU] & 0x80) != 0) ||
				(lpKeyState.Length > VirtualKeys.VK_RCONTROL && (lpKeyState[VirtualKeys.VK_RCONTROL] & 0x80) != 0
				 && lpKeyState.Length > VirtualKeys.VK_LMENU && (lpKeyState[VirtualKeys.VK_LMENU] & 0x80) != 0);
		}

		/// <summary>
		/// Dead-key-aware counterpart to <see cref="ToUnicode"/>, used by the input-collection hook so
		/// that dead keys compose on Linux/macOS the way they already do on Windows. Routes through the
		/// active layout's stateful translator (which maintains dead-key composition state) and falls
		/// back to the stateless <see cref="ToUnicode"/> if the provider can't translate the key.
		/// Return convention matches Windows ToUnicode: &gt;0 chars, 0 no text, &lt;0 dead key.
		/// </summary>
		public static int ToUnicodeWithDeadKeys(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] char[] pwszBuff, uint wFlags, nint dwhkl)
		{
			if (pwszBuff == null || pwszBuff.Length == 0)
				return 0;

			DeriveModifierState(lpKeyState, out var shift, out var caps, out var altGr);
			var n = KeyCodes.TranslateKeyWithDeadKeys(wVirtKey, shift, altGr, caps, pwszBuff.AsSpan());

			if (n != KeyCodes.TranslateNotHandled)
				return n;

			// No layout-aware provider available: fall back to the stateless mapping (no dead keys).
			return ToUnicode(wVirtKey, wScanCode, lpKeyState, pwszBuff, wFlags, dwhkl);
		}

		public static int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] char[] pwszBuff, uint wFlags, nint dwhkl)
		{
			// Best-effort VK?char mapping for Linux; limited to US-style keys.
			// Similar to Windows ToUnicodeEx but without dead-key handling.
			if (pwszBuff == null || pwszBuff.Length == 0)
				return 0;

			DeriveModifierState(lpKeyState, out var shift, out var caps, out var altGr);

			// Prefer the active OS keyboard layout (handles non-US layouts and non-ASCII
			// characters such as "ä"); fall back to the hardcoded US table below if the
			// platform's char mapper can't translate this key. Caps Lock only affects
			// letter keys, so it's applied as a post-hoc case toggle rather than XOR'd
			// into the shift state passed to the mapper (which would also affect
			// digit/symbol keys, e.g. turning '2' into '"' on Estonian layouts).
			if (KeyCodes.TryMapKeystrokeToRune(wVirtKey, shift, altGr, out var mappedRune))
			{
				if (caps && Rune.IsLetter(mappedRune))
				{
					var upper = Rune.ToUpperInvariant(mappedRune);
					var lower = Rune.ToLowerInvariant(mappedRune);
					mappedRune = mappedRune == upper ? lower : upper;
				}

				var written = mappedRune.EncodeToUtf16(pwszBuff);
				return written;
			}

			char ch = '\0';

			// Letters
			if (wVirtKey is >= (uint)'A' and <= (uint)'Z')
			{
				bool upper = shift ^ caps;
				ch = (char)(upper ? wVirtKey : (wVirtKey + 32)); // make lowercase by adding 32
			}
			// Digits and shifted symbols on the number row
			else if (wVirtKey is >= (uint)'0' and <= (uint)'9')
			{
				if (!shift)
				{
					ch = (char)wVirtKey;
				}
				else
				{
					// US keyboard shifted digits
					ch = wVirtKey switch
					{
						'1' => '!',
						'2' => '@',
						'3' => '#',
						'4' => '$',
						'5' => '%',
						'6' => '^',
						'7' => '&',
						'8' => '*',
						'9' => '(',
						'0' => ')',
						_ => '\0'
					};
				}
			}
			else
			{
				// Common punctuation / OEM keys (US layout)
				ch = wVirtKey switch
				{
					VirtualKeys.VK_SPACE => ' ',
					VirtualKeys.VK_OEM_MINUS => shift ? '_' : '-',
					VirtualKeys.VK_OEM_PLUS => shift ? '+' : '=',
					VirtualKeys.VK_OEM_1 => shift ? ':' : ';',
					VirtualKeys.VK_OEM_2 => shift ? '?' : '/',
					VirtualKeys.VK_OEM_3 => shift ? '~' : '`',
					VirtualKeys.VK_OEM_4 => shift ? '{' : '[',
					VirtualKeys.VK_OEM_5 => shift ? '|' : '\\',
					VirtualKeys.VK_OEM_6 => shift ? '}' : ']',
					VirtualKeys.VK_OEM_7 => shift ? '"' : '\'',
					VirtualKeys.VK_OEM_COMMA => shift ? '<' : ',',
					VirtualKeys.VK_OEM_PERIOD => shift ? '>' : '.',
					_ => '\0'
				};
			}

			if (ch == '\0')
				return 0;

			pwszBuff[0] = ch;
			return 1;
		}

		public static uint MapVirtualKeyToChar(uint wVirtKey, nint hkl)
		{
			// Return the character code corresponding to the given virtual key.
			// Similar to Windows MapVirtualKeyEx with MAPVK_VK_TO_CHAR.
			char[] ch = new char[1];
			int result = ToUnicode(wVirtKey, 0, new byte[256], ch, 0, hkl);
			if (result > 0)
				return ch[0];
			else
				return 0;
		}

		public static bool SetDllDirectory(string path)
		{
			var pathVariableName =
#if OSX
				"DYLD_LIBRARY_PATH";
#else
				"LD_LIBRARY_PATH";
#endif

			if (path == null)
			{
				Environment.SetEnvironmentVariable(pathVariableName, Script.TheScript.ldLibraryPath);
				return Environment.GetEnvironmentVariable(pathVariableName) == Script.TheScript.ldLibraryPath;
			}
			else
			{
				var append = path;
				var orig = Environment.GetEnvironmentVariable(pathVariableName) ?? "";
				var newPath = "";

				if (orig != "")
				{
					append = ":" + append;
					newPath = orig + append;
					//newPath = "\"" + orig + "\"" + append;//Unsure if quotes are needed.
				}
				else
					newPath = append;

				Environment.SetEnvironmentVariable(pathVariableName, newPath);
				return Environment.GetEnvironmentVariable(pathVariableName).EndsWith(append);
			}
		}

		public static nint LoadLibrary(string path) => NativeLibrary.TryLoad(path, out var module) ? module : 0;

#if OSX
		[LibraryImport("libSystem.dylib")]
		private static partial int pthread_threadid_np(IntPtr thread, out ulong threadid);
#endif

		public static uint CurrentThreadId()
		{
#if OSX
			_ = pthread_threadid_np(IntPtr.Zero, out var tid);
			return (uint)tid;
#else
			return (uint)Xlib.gettid();
#endif
		}

		public static bool DestroyIcon(nint icon) =>
#if OSX
			true;
#else
			Xlib.GdipDisposeImage(icon) == 0;//Unsure if this works or is even needed on linux.
#endif

		public static bool ExitProgram(uint flags, uint reason)
		{
#if OSX
			Ks.OutputDebugLine("ExitProgram is not implemented for macOS yet.");
			return false;
#else
			var cmd = "";
			var force = false;

			//Taken from this article: https://fostips.com/log-out-command-linux-desktops/
			if ((flags & 4) == 4)
			{
				force = true;//Close all programs.
			}

			if (flags == 0)//Logoff.
			{
				if (isGnome)
				{
					if (force)
						cmd = "gnome-session-quit --force";
					else
						cmd = "gnome-session-quit";
				}
				else if (isKde)
				{
					if (force)
						cmd = "qdbus org.kde.ksmserver /KSMServer logout 0 0 2";
					else
						cmd = "qdbus org.kde.ksmserver /KSMServer logout 1 0 3";
				}
				else if (isXfce)
				{
					if (force)
						cmd = "xfce4-session-logout --fast";
					else
						cmd = "xfce4-session-logout";
				}
				else if (isMate)
				{
					if (force)
						cmd = "mate-session-save --logout --force";
					else
						cmd = "mate-session-save --logout";
				}
				else if (isCinnamon)
				{
					if (force)
						cmd = "cinnamon-session-quit --no-prompt";
					else
						cmd = "cinnamon-session-quit";
				}
				else if (IsLxqt)
				{
					if (force)
						Ks.OutputDebugLine($"LXQT doesn't support forced logouts.");

					cmd = "lxqt-leave";
				}
				else if (IsLxde)
				{
					if (force)
						Ks.OutputDebugLine($"LXDE doesn't support forced logouts.");

					cmd = "lxde-logout";
				}

				if (!string.IsNullOrWhiteSpace(cmd) && cmd.Bash() != 0)
					Ks.OutputDebugLine($"ExitProgram logoff command failed: {cmd}");
			}
			else if ((flags & 1) == 1)//Halt/shutdown.
			{
				if ((flags & 8) == 8)//Power down.
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
			else if ((flags & 2) == 2)//Reboot.
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
			else if ((flags & 8) == 8)//Shutdown.
			{
				if ("shutdown now".Bash() != 0)
					Ks.OutputDebugLine("ExitProgram shutdown command failed: shutdown now");
			}

			return true;
#endif
		}

		public static bool UnregisterHotKey(nint hWnd, uint id) => true;

		public static bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => true;

		public static bool PostMessage(nint hWnd, uint msg, uint wParam, uint lParam) => true;

		public static bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam) => true;

		public static bool RegisterHotKey(nint hWnd, uint id, KeyModifiers fsModifiers, uint vk) => true;

		public static bool GetCursorPos(out POINT lpPoint)
		{
#if LINUX
			if (IsWaylandSession)
			{
				// The core Wayland protocol forbids foreign clients from querying the global cursor
				// position, so it is only available two ways: from a compositor-IPC backend (KWin via
				// D-Bus, sway/hyprland via their JSON sockets, COSMIC via its Wayland extension), or --
				// for an absolute-positioning device (tablet, touchpad, VMware's virtual mouse) -- from
				// inputd's last absolute report. If neither is available (e.g. labwc/GNOME with a plain
				// relative mouse) the position is genuinely unknowable, so fail instead of guessing.
				var backend = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current;

				if (backend != null && backend.TryGetCursorPos(out var bx, out var by))
				{
					lpPoint = new POINT(bx, by);
					return true;
				}

				// TryGetPointerPosition fails when the last movement was relative (the daemon marks the
				// absolute position invalid). Safe to reach from the inputd hook reader thread: the query
				// channel only carries quick queries/synthesis (synthesis acks on enqueue) and hook
				// callbacks run on the daemon's dedicated lanes, so a position query never blocks on hook
				// processing.
				if (KeysharpInputdManager.TryGetPointerPosition(
						out var rawX,
						out var rawY,
						out var minX,
						out var maxX,
						out var minY,
						out var maxY)
					&& TryScalePointerAxis(rawX, minX, maxX, (int)A_ScreenWidth, out var x)
					&& TryScalePointerAxis(rawY, minY, maxY, (int)A_ScreenHeight, out var y))
				{
					lpPoint = new POINT(x, y);
					return true;
				}

				lpPoint = default;
				return false;
			}

			// X11: query the server directly. Pixel-accurate, and safe to call from any thread
			// (including the inputd hook reader thread) because it uses the per-thread [ThreadStatic]
			// XDisplay connection rather than the UI-thread-affine Eto/GTK Forms.Mouse.Position.
			return TryGetX11CursorPos(out lpPoint);
#else
			var pos = Forms.Mouse.Position;
			lpPoint = new POINT(Convert.ToInt32(pos.X), Convert.ToInt32(pos.Y));
			return true;
#endif
		}

#if LINUX
		// Reads the pointer position straight from the X server (root-window coordinates = the virtual
		// desktop, origin 0,0). Safe from any thread via the per-thread [ThreadStatic] XDisplay handle.
		private static bool TryGetX11CursorPos(out POINT p)
		{
			p = default;

			try
			{
				var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

				if (display == null || display.Handle == 0)
					return false;

				var root = Keysharp.Internals.Window.Linux.X11.Xlib.XDefaultRootWindow(display.Handle);

				if (Keysharp.Internals.Window.Linux.X11.Xlib.XQueryPointer(display.Handle, root,
						out _, out _, out var rootX, out var rootY, out _, out _, out _))
				{
					p = new POINT(rootX, rootY);
					return true;
				}
			}
			catch
			{
			}

			return false;
		}
#endif

#if OSX
		/// <summary>
		/// Maps AHK-style cursor names to their corresponding <see cref="NSCursor"/> objects.<br/>
		/// Names not present here (e.g. AppStarting, Help, Icon, Size, SizeAll, SizeNESW, SizeNWSE, UpArrow, Wait)
		/// have no direct AppKit equivalent and are reported as Unknown.
		/// </summary>
		private static readonly Dictionary<string, Func<NSCursor>> cursorMap = new (StringComparer.OrdinalIgnoreCase)
		{
			{ "Arrow", () => NSCursor.ArrowCursor },
			{ "IBeam", () => NSCursor.IBeamCursor },
			{ "Cross", () => NSCursor.CrosshairCursor },
			{ "No", () => NSCursor.OperationNotAllowedCursor },
			{ "SizeWE", () => NSCursor.ResizeLeftRightCursor },
			{ "SizeNS", () => NSCursor.ResizeUpDownCursor }
		};

		public static string GetCursor()
		{
			var current = NSCursor.CurrentSystemCursor;

			if (current != null)
				foreach (var (name, cursor) in cursorMap)
					if (current.Handle == cursor().Handle)
						return name;

			return "Unknown";
		}

		public static void SetCursor(string cursorName)
		{
			if (cursorMap.TryGetValue(cursorName, out var cursor))
				cursor().Set();
		}

#else
		public static string GetCursor() => "Unknown";

		public static void SetCursor(string cursorName)
		{
		}
#endif

#if LINUX
		private static bool TryScalePointerAxis(int value, int min, int max, int size, out int scaled)
		{
			scaled = 0;

			if (max <= min || size <= 0)
				return false;

			var clamped = Math.Clamp(value, min, max);
			scaled = (int)Math.Round((double)(clamped - min) * (size - 1) / (max - min));
			return true;
		}
#endif
		}
	}
#endif
