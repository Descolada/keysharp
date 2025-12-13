// Replace ONLY the LinuxCharMapper in your TextMapping file with this version.
// Key change: no P/Invoke to xkb_keysym_from_utf32. We compute keysyms ourselves.
// Rule used (X11 keysym encoding):
//   - ASCII (U+0020..U+007E) -> keysym == codepoint
//   - Latin-1 (U+00A0..U+00FF) -> keysym == codepoint
//   - Otherwise -> keysym == 0x01000000 | codepoint
// This avoids the missing symbol error while remaining compatible with libxkbcommon.

#if LINUX
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Linux
{
	partial class LinuxKeyboardMouseSender
	{
		internal static class LinuxCharMapper
		{
			private static readonly object _initLock = new();
			private static bool _initTried;
			private static bool _ready;

			// Optional layout preferences settable via ConfigureLayout(...)
			private static string? _prefRules, _prefModel, _prefLayout, _prefVariant, _prefOptions;

			// libxkbcommon handles
			private static IntPtr _ctx    = IntPtr.Zero;   // xkb_context*
			private static IntPtr _keymap = IntPtr.Zero;   // xkb_keymap*

			// Optional X11 (disabled by default)
			private static bool   _tryX11 = false;
			private static bool   _xThreadsInited = false;
			private static IntPtr _xDisplay = IntPtr.Zero;

			// Ranges & mods
			private static int  _minKeycode, _maxKeycode;
			private static uint _shiftIndex = uint.MaxValue;
			private static uint _mod5Index  = uint.MaxValue; // often AltGr
			private static bool _hasModsForLevel;

			// Cache: keysym -> (vk, needShift, needAltGr)
			private static readonly Dictionary<uint, (uint vk, bool s, bool g)> _cache = new(256);

			private static readonly Dictionary<string, uint> _xkbName2Vk = new(StringComparer.Ordinal)
			{
				["TLDE"] = VK_OEM_3,
				["AE01"] = (uint)'1', ["AE02"] = (uint)'2', ["AE03"] = (uint)'3', ["AE04"] = (uint)'4',
				["AE05"] = (uint)'5', ["AE06"] = (uint)'6', ["AE07"] = (uint)'7', ["AE08"] = (uint)'8',
				["AE09"] = (uint)'9', ["AE10"] = (uint)'0', ["AE11"] = VK_OEM_MINUS, ["AE12"] = VK_OEM_PLUS,

				["AD01"] = (uint)'Q', ["AD02"] = (uint)'W', ["AD03"] = (uint)'E', ["AD04"] = (uint)'R',
				["AD05"] = (uint)'T', ["AD06"] = (uint)'Y', ["AD07"] = (uint)'U', ["AD08"] = (uint)'I',
				["AD09"] = (uint)'O', ["AD10"] = (uint)'P', ["AD11"] = VK_OEM_4,  ["AD12"] = VK_OEM_6,

				["AC01"] = (uint)'A', ["AC02"] = (uint)'S', ["AC03"] = (uint)'D', ["AC04"] = (uint)'F',
				["AC05"] = (uint)'G', ["AC06"] = (uint)'H', ["AC07"] = (uint)'J', ["AC08"] = (uint)'K',
				["AC09"] = (uint)'L', ["AC10"] = VK_OEM_1,  ["AC11"] = VK_OEM_7,  ["BKSL"] = VK_OEM_5,

				["AB01"] = (uint)'Z', ["AB02"] = (uint)'X', ["AB03"] = (uint)'C', ["AB04"] = (uint)'V',
				["AB05"] = (uint)'B', ["AB06"] = (uint)'N', ["AB07"] = (uint)'M', ["AB08"] = VK_OEM_COMMA,
				["AB09"] = VK_OEM_PERIOD, ["AB10"] = VK_OEM_2,

				["SPCE"] = VK_SPACE,
			};

			public static bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr)
			{
				vk = 0; needShift = false; needAltGr = false;

				EnsureInitialized();
				if (!_ready)
				{
					// Fallback to US fast map if xkbcommon couldn't init
					return TextFastMapUS.TryMapCharToVkShift_US(rune, out vk, out needShift);
				}

				// Convert Rune to X11 keysym value without calling into libxkbcommon:
				// ASCII and Latin-1 (0xA0..0xFF) are direct; others use 0x01000000 | codepoint.
				uint ks = KeysymFromRune(rune);
				if (ks == 0)
					return false;

				// Cache?
				if (_cache.TryGetValue(ks, out var m))
				{
					(vk, needShift, needAltGr) = m;
					return true;
				}

				// Scan all keys & levels on layout 0
				for (uint key = (uint)_minKeycode; key <= (uint)_maxKeycode; key++)
				{
					var name = xkb_keymap_key_get_name_safe(_keymap, key);
					if (string.IsNullOrEmpty(name))
						continue;

					if (!_xkbName2Vk.TryGetValue(name!, out var candidateVk))
						continue;

					uint layout = 0;
					int levels = xkb_keymap_num_levels_for_key(_keymap, key, layout);
					if (levels <= 0) continue;

					for (uint level = 0; level < (uint)levels; level++)
					{
						if (!LevelHasKeysym(_keymap, key, layout, level, ks))
							continue;

						(bool s, bool g) = ResolveModsForLevel(key, layout, level);
						_cache[ks] = (candidateVk, s, g);
						vk = candidateVk; needShift = s; needAltGr = g;
						return true;
					}
				}

				return false;
			}

			/// <summary>
			/// Optional: call early to set rules/model/layout/variant/options (no X11 needed).
			/// </summary>
			public static void ConfigureLayout(string? rules = null, string? model = null, string? layout = null, string? variant = null, string? options = null)
			{
				lock (_initLock)
				{
					_prefRules   = rules;
					_prefModel   = model;
					_prefLayout  = layout;
					_prefVariant = variant;
					_prefOptions = options;

					if (_keymap != IntPtr.Zero) { xkb_keymap_unref(_keymap); _keymap = IntPtr.Zero; }
					if (_ctx    != IntPtr.Zero) { xkb_context_unref(_ctx);   _ctx    = IntPtr.Zero; }
					if (_xDisplay != IntPtr.Zero) { XCloseDisplay(_xDisplay); _xDisplay = IntPtr.Zero; }
					_cache.Clear();
					_ready = _initTried = false;
				}
			}

			/// <summary>
			/// Expose the current xkb_keymap pointer (or IntPtr.Zero if unavailable).
			/// Useful for callers that need a layout token similar to Windows' HKL.
			/// </summary>
			internal static nint GetCurrentKeymapHandle()
			{
				EnsureInitialized();
				return _keymap;
			}

			private static uint KeysymFromRune(Rune r)
			{
				uint cp = (uint)r.Value;

				// ASCII printable & common controls we might type:
				if (cp <= 0x7F)
					return cp;

				// Latin-1 range has direct keysyms (not 0x01000000-prefixed)
				if (cp >= 0x00A0 && cp <= 0x00FF)
					return cp;

				// Generic Unicode keysym encoding
				if (cp >= 0x0100 && cp <= 0x10FFFF)
					return 0x01000000u | cp;

				return 0;
			}

			private static void EnsureInitialized()
			{
				if (_ready || _initTried) return;

				lock (_initLock)
				{
					if (_ready || _initTried) return;
					_initTried = true;

					try
					{
						_tryX11 = string.Equals(Environment.GetEnvironmentVariable("KEYSHARP_XKB_USE_X11"), "1", StringComparison.Ordinal);

						_ctx = xkb_context_new(0);
						if (_ctx == IntPtr.Zero)
							return;

						if (BuildKeymapFromNames())
						{
							FinalizeKeymapCommon();
							_ready = true;
							return;
						}

						// Optional X11 fallback (OFF by default)
						if (_tryX11)
						{
							if (!_xThreadsInited)
							{
								try { XInitThreads(); _xThreadsInited = true; } catch { }
							}
							if (SetupKeymapFromX11())
							{
								FinalizeKeymapCommon();
								_ready = true;
								return;
							}
						}
					}
					catch
					{
						_ready = false;
					}
				}
			}

			private static bool BuildKeymapFromNames()
			{
				string? rules   = _prefRules   ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_RULES");
				string? model   = _prefModel   ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_MODEL");
				string? layout  = _prefLayout  ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_LAYOUT");
				string? variant = _prefVariant ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_VARIANT");
				string? options = _prefOptions ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_OPTIONS");

				var names = new xkb_rule_names();
				var pins = new List<IntPtr>();

				try
				{
					if (!string.IsNullOrEmpty(rules))   names.rules   = AllocUtf8(rules!, pins);
					if (!string.IsNullOrEmpty(model))   names.model   = AllocUtf8(model!, pins);
					if (!string.IsNullOrEmpty(layout))  names.layout  = AllocUtf8(layout!, pins);
					if (!string.IsNullOrEmpty(variant)) names.variant = AllocUtf8(variant!, pins);
					if (!string.IsNullOrEmpty(options)) names.options = AllocUtf8(options!, pins);

					_keymap = xkb_keymap_new_from_names(_ctx, ref names, 0);
					return _keymap != IntPtr.Zero;
				}
				finally
				{
					foreach (var p in pins) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
				}
			}

			private static bool SetupKeymapFromX11()
			{
				try
				{
					// Best-effort; wrapped to avoid the glibc assert crash. Only used if KEYSHARP_XKB_USE_X11=1
					_ = xkb_x11_setup_xkb_extension(_xDisplay, 1, 0, 0, out _, out _, out _, out _);

					int deviceId = xkb_x11_get_core_keyboard_device_id(_xDisplay);
					if (deviceId <= 0)
						return false;

					_keymap = xkb_x11_keymap_new_from_device(_ctx, _xDisplay, deviceId, 0);
					return _keymap != IntPtr.Zero;
				}
				catch
				{
					return false;
				}
			}

			private static void FinalizeKeymapCommon()
			{
				_minKeycode = xkb_keymap_min_keycode(_keymap);
				_maxKeycode = xkb_keymap_max_keycode(_keymap);

				_shiftIndex = xkb_keymap_mod_get_index(_keymap, "Shift");

				_mod5Index = xkb_keymap_mod_get_index(_keymap, "Mod5");
				if (_mod5Index == uint.MaxValue)
				{
					_mod5Index = xkb_keymap_mod_get_index(_keymap, "AltGr");
					if (_mod5Index == uint.MaxValue)
						_mod5Index = xkb_keymap_mod_get_index(_keymap, "ISO_Level3_Shift");
				}

				try
				{
					xkb_keymap_key_get_mods_for_level(_keymap, 0, 0, 0, IntPtr.Zero, UIntPtr.Zero);
					_hasModsForLevel = true;
				}
				catch (EntryPointNotFoundException)
				{
					_hasModsForLevel = false;
				}
			}

			private static (bool needShift, bool needAltGr) ResolveModsForLevel(uint key, uint layout, uint level)
			{
				if (_hasModsForLevel)
				{
					Span<ulong> buf = stackalloc ulong[4];
					int got;
					unsafe
					{
						fixed (ulong* p = buf)
							got = xkb_keymap_key_get_mods_for_level(_keymap, key, layout, level, (IntPtr)p, (UIntPtr)buf.Length);
					}
					if (got > 0)
					{
						ulong mask = buf[0];
						bool s = (_shiftIndex != uint.MaxValue) && ((mask & (1UL << (int)_shiftIndex)) != 0);
						bool g = (_mod5Index  != uint.MaxValue) && ((mask & (1UL << (int)_mod5Index))  != 0);
						return (s, g);
					}
				}

				// Heuristic when the function isn't available
				return level switch
				{
					0 => (false, false),
					1 => (true,  false),
					2 => (false, true ),
					3 => (true,  true ),
					_ => (false, false)
				};
			}

			private static bool LevelHasKeysym(IntPtr keymap, uint key, uint layout, uint level, uint target)
			{
				IntPtr symsPtr;
				int count = xkb_keymap_key_get_syms_by_level(keymap, key, layout, level, out symsPtr);
				if (count <= 0 || symsPtr == IntPtr.Zero)
					return false;

				unsafe
				{
					var syms = (uint*)symsPtr;
					for (int i = 0; i < count; i++)
						if (syms[i] == target)
							return true;
				}
				return false;
			}

			private static unsafe string? xkb_keymap_key_get_name_safe(IntPtr keymap, uint key)
			{
				var ptr = xkb_keymap_key_get_name(keymap, key);
				return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
			}

			private static IntPtr AllocUtf8(string s, List<IntPtr> pins)
			{
				var bytes = Encoding.UTF8.GetBytes(s + "\0");
				var mem = Marshal.AllocHGlobal(bytes.Length);
				Marshal.Copy(bytes, 0, mem, bytes.Length);
				pins.Add(mem);
				return mem;
			}

			// ------------ P/Invoke (no xkb_keysym_from_utf32 here) ------------
			private const string LibXkbCommon    = "libxkbcommon.so.0";
			private const string LibXkbCommonX11 = "libxkbcommon-x11.so.0";
			private const string LibX11          = "libX11.so.6";

			[StructLayout(LayoutKind.Sequential)]
			private struct xkb_rule_names
			{
				public IntPtr rules, model, layout, variant, options;
			}

			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_context_new(uint flags);
			[DllImport(LibXkbCommon)] private static extern void   xkb_context_unref(IntPtr ctx);

			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_keymap_new_from_names(IntPtr ctx, ref xkb_rule_names names, uint flags);
			[DllImport(LibXkbCommon)] private static extern void   xkb_keymap_unref(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern int    xkb_keymap_min_keycode(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern int    xkb_keymap_max_keycode(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern uint   xkb_keymap_mod_get_index(IntPtr keymap, string name);
			[DllImport(LibXkbCommon)] private static extern int    xkb_keymap_num_levels_for_key(IntPtr keymap, uint key, uint layout);
			[DllImport(LibXkbCommon)] private static extern int    xkb_keymap_key_get_syms_by_level(IntPtr keymap, uint key, uint layout, uint level, out IntPtr syms_out);
			[DllImport(LibXkbCommon, EntryPoint = "xkb_keymap_key_get_mods_for_level")]
			private static extern int xkb_keymap_key_get_mods_for_level(IntPtr keymap, uint key, uint layout, uint level, IntPtr masks_out, UIntPtr masks_out_size);
			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_keymap_key_get_name(IntPtr keymap, uint key);

			// Optional X11 path
			[DllImport(LibX11)] private static extern int    XInitThreads();
			[DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
			[DllImport(LibX11)] private static extern int    XCloseDisplay(IntPtr display);

			[DllImport(LibXkbCommonX11)] private static extern int    xkb_x11_setup_xkb_extension(IntPtr dpy, int major, int minor, int flags, out int major_out, out int minor_out, out int base_event_out, out int base_error_out);
			[DllImport(LibXkbCommonX11)] private static extern int    xkb_x11_get_core_keyboard_device_id(IntPtr dpy);
			[DllImport(LibXkbCommonX11)] private static extern IntPtr xkb_x11_keymap_new_from_device(IntPtr ctx, IntPtr dpy, int device_id, uint flags);

			// Cleanup
			static LinuxCharMapper()
			{
				AppDomain.CurrentDomain.ProcessExit += (_, __) =>
				{
					if (_keymap   != IntPtr.Zero) { xkb_keymap_unref(_keymap); _keymap = IntPtr.Zero; }
					if (_ctx      != IntPtr.Zero) { xkb_context_unref(_ctx);   _ctx    = IntPtr.Zero; }
					if (_xDisplay != IntPtr.Zero) { XCloseDisplay(_xDisplay);  _xDisplay = IntPtr.Zero; }
				};
			}
		}

		private static class TextFastMapUS
		{
			public static bool TryMapCharToVkShift_US(Rune rune, out uint vk, out bool needShift)
			{
				vk = 0; needShift = false;
				if (!rune.IsAscii) return false;
				char ch = (char)rune.Value;

				switch (ch)
				{
					case ' ': vk = VK_SPACE; return true;
					case '\t': vk = VK_TAB; return true;
					case '\r':
					case '\n': vk = VK_RETURN; return true;
					case '\b': vk = VK_BACK; return true;
				}
				if (ch is >= 'a' and <= 'z') { vk = (uint)char.ToUpperInvariant(ch); return true; }
				if (ch is >= 'A' and <= 'Z') { vk = (uint)ch; needShift = true; return true; }
				if (ch is >= '0' and <= '9') { vk = (uint)ch; return true; }

				switch (ch)
				{
					case '!': vk=(uint)'1'; needShift=true; return true;
					case '@': vk=(uint)'2'; needShift=true; return true;
					case '#': vk=(uint)'3'; needShift=true; return true;
					case '$': vk=(uint)'4'; needShift=true; return true;
					case '%': vk=(uint)'5'; needShift=true; return true;
					case '^': vk=(uint)'6'; needShift=true; return true;
					case '&': vk=(uint)'7'; needShift=true; return true;
					case '*': vk=(uint)'8'; needShift=true; return true;
					case '(': vk=(uint)'9'; needShift=true; return true;
					case ')': vk=(uint)'0'; needShift=true; return true;

					case '-': vk=VK_OEM_MINUS; return true;
					case '_': vk=VK_OEM_MINUS; needShift=true; return true;

					case '=': vk=VK_OEM_PLUS; return true;
					case '+': vk=VK_OEM_PLUS; needShift=true; return true;

					case '[': vk=VK_OEM_4; return true;
					case '{': vk=VK_OEM_4; needShift=true; return true;

					case ']': vk=VK_OEM_6; return true;
					case '}': vk=VK_OEM_6; needShift=true; return true;

					case '\\': vk=VK_OEM_5; return true;
					case '|':  vk=VK_OEM_5; needShift=true; return true;

					case ';': vk=VK_OEM_1; return true;
					case ':': vk=VK_OEM_1; needShift=true; return true;

					case '\'': vk=VK_OEM_7; return true;
					case '"':  vk=VK_OEM_7; needShift=true; return true;

					case ',': vk=VK_OEM_COMMA; return true;
					case '<': vk=VK_OEM_COMMA; needShift=true; return true;

					case '.': vk=VK_OEM_PERIOD; return true;
					case '>': vk=VK_OEM_PERIOD; needShift=true; return true;

					case '/': vk=VK_OEM_2; return true;
					case '?': vk=VK_OEM_2; needShift=true; return true;

					case '`': vk=VK_OEM_3; return true;
					case '~': vk=VK_OEM_3; needShift=true; return true;
				}
				return false;
			}
		}
	}
}
#endif
