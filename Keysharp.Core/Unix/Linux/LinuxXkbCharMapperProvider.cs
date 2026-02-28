#if LINUX
using System.Runtime.InteropServices;

using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Unix
{
	partial class UnixKeyboardMouseSender
	{
		private sealed class LinuxXkbCharMapperProvider : IUnixCharMapperProvider
		{
			private readonly object initLock = new();

			private bool initTried;
			private bool ready;

			private string prefRules;
			private string prefModel;
			private string prefLayout;
			private string prefVariant;
			private string prefOptions;

			private IntPtr ctx;
			private IntPtr keymap;

			private int minKeycode;
			private int maxKeycode;
			private uint shiftIndex = uint.MaxValue;
			private uint mod5Index = uint.MaxValue;
			private bool hasModsForLevel;

			private readonly Dictionary<uint, (uint vk, bool s, bool g)> cache = new(256);

			private static readonly Dictionary<string, uint> xkbName2Vk = new(StringComparer.Ordinal)
			{
				["TLDE"] = VK_OEM_3,
				["AE01"] = (uint)'1', ["AE02"] = (uint)'2', ["AE03"] = (uint)'3', ["AE04"] = (uint)'4',
				["AE05"] = (uint)'5', ["AE06"] = (uint)'6', ["AE07"] = (uint)'7', ["AE08"] = (uint)'8',
				["AE09"] = (uint)'9', ["AE10"] = (uint)'0', ["AE11"] = VK_OEM_MINUS, ["AE12"] = VK_OEM_PLUS,

				["AD01"] = (uint)'Q', ["AD02"] = (uint)'W', ["AD03"] = (uint)'E', ["AD04"] = (uint)'R',
				["AD05"] = (uint)'T', ["AD06"] = (uint)'Y', ["AD07"] = (uint)'U', ["AD08"] = (uint)'I',
				["AD09"] = (uint)'O', ["AD10"] = (uint)'P', ["AD11"] = VK_OEM_4, ["AD12"] = VK_OEM_6,

				["AC01"] = (uint)'A', ["AC02"] = (uint)'S', ["AC03"] = (uint)'D', ["AC04"] = (uint)'F',
				["AC05"] = (uint)'G', ["AC06"] = (uint)'H', ["AC07"] = (uint)'J', ["AC08"] = (uint)'K',
				["AC09"] = (uint)'L', ["AC10"] = VK_OEM_1, ["AC11"] = VK_OEM_7, ["BKSL"] = VK_OEM_5,

				["AB01"] = (uint)'Z', ["AB02"] = (uint)'X', ["AB03"] = (uint)'C', ["AB04"] = (uint)'V',
				["AB05"] = (uint)'B', ["AB06"] = (uint)'N', ["AB07"] = (uint)'M', ["AB08"] = VK_OEM_COMMA,
				["AB09"] = VK_OEM_PERIOD, ["AB10"] = VK_OEM_2,

				["SPCE"] = VK_SPACE,
			};

			public bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr)
			{
				vk = 0;
				needShift = false;
				needAltGr = false;

				EnsureInitialized();

				if (!ready || keymap == IntPtr.Zero)
					return false;

				uint keysym = KeysymFromRune(rune);

				if (keysym == 0)
					return false;

				if (cache.TryGetValue(keysym, out var cached))
				{
					(vk, needShift, needAltGr) = cached;
					return true;
				}

				for (uint key = (uint)minKeycode; key <= (uint)maxKeycode; key++)
				{
					var name = xkb_keymap_key_get_name_safe(keymap, key);

					if (string.IsNullOrEmpty(name))
						continue;

					if (!xkbName2Vk.TryGetValue(name, out var candidateVk))
						continue;

					const uint layout = 0;
					int levels = xkb_keymap_num_levels_for_key(keymap, key, layout);

					if (levels <= 0)
						continue;

					for (uint level = 0; level < (uint)levels; level++)
					{
						if (!LevelHasKeysym(keymap, key, layout, level, keysym))
							continue;

						(bool s, bool g) = ResolveModsForLevel(key, layout, level);
						cache[keysym] = (candidateVk, s, g);
						vk = candidateVk;
						needShift = s;
						needAltGr = g;
						return true;
					}
				}

				return false;
			}

			public void ConfigureLayout(string rules, string model, string layout, string variant, string options)
			{
				lock (initLock)
				{
					prefRules = rules;
					prefModel = model;
					prefLayout = layout;
					prefVariant = variant;
					prefOptions = options;
					ResetState();
				}
			}

			public nint GetCurrentKeymapHandle()
			{
				EnsureInitialized();
				return keymap;
			}

			public void Dispose()
			{
				lock (initLock)
					ResetState();
			}

			private void EnsureInitialized()
			{
				if (ready || initTried)
					return;

				lock (initLock)
				{
					if (ready || initTried)
						return;

					initTried = true;

					try
					{
						ctx = xkb_context_new(0);

						if (ctx == IntPtr.Zero)
							return;

						if (BuildKeymapFromNames())
						{
							FinalizeKeymapCommon();
							ready = true;
							return;
						}

						ResetNativeHandles();
					}
					catch
					{
						ready = false;
						ResetNativeHandles();
					}
				}
			}

			private bool BuildKeymapFromNames()
			{
				string rules = prefRules ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_RULES");
				string model = prefModel ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_MODEL");
				string layout = prefLayout ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_LAYOUT");
				string variant = prefVariant ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_VARIANT");
				string options = prefOptions ?? Environment.GetEnvironmentVariable("XKB_DEFAULT_OPTIONS");

				var names = new xkb_rule_names();
				var pins = new List<IntPtr>();

				try
				{
					if (!string.IsNullOrEmpty(rules)) names.rules = AllocUtf8(rules, pins);
					if (!string.IsNullOrEmpty(model)) names.model = AllocUtf8(model, pins);
					if (!string.IsNullOrEmpty(layout)) names.layout = AllocUtf8(layout, pins);
					if (!string.IsNullOrEmpty(variant)) names.variant = AllocUtf8(variant, pins);
					if (!string.IsNullOrEmpty(options)) names.options = AllocUtf8(options, pins);

					keymap = xkb_keymap_new_from_names(ctx, ref names, 0);
					return keymap != IntPtr.Zero;
				}
				finally
				{
					foreach (var ptr in pins)
						if (ptr != IntPtr.Zero)
							Marshal.FreeHGlobal(ptr);
				}
			}

			private void FinalizeKeymapCommon()
			{
				minKeycode = xkb_keymap_min_keycode(keymap);
				maxKeycode = xkb_keymap_max_keycode(keymap);
				shiftIndex = xkb_keymap_mod_get_index(keymap, "Shift");

				mod5Index = xkb_keymap_mod_get_index(keymap, "Mod5");

				if (mod5Index == uint.MaxValue)
				{
					mod5Index = xkb_keymap_mod_get_index(keymap, "AltGr");

					if (mod5Index == uint.MaxValue)
						mod5Index = xkb_keymap_mod_get_index(keymap, "ISO_Level3_Shift");
				}

				try
				{
					xkb_keymap_key_get_mods_for_level(keymap, 0, 0, 0, IntPtr.Zero, UIntPtr.Zero);
					hasModsForLevel = true;
				}
				catch (EntryPointNotFoundException)
				{
					hasModsForLevel = false;
				}
			}

			private (bool needShift, bool needAltGr) ResolveModsForLevel(uint key, uint layout, uint level)
			{
				if (hasModsForLevel)
				{
					Span<ulong> buf = stackalloc ulong[4];
					int got;

					unsafe
					{
						fixed (ulong* p = buf)
							got = xkb_keymap_key_get_mods_for_level(keymap, key, layout, level, (IntPtr)p, (UIntPtr)buf.Length);
					}

					if (got > 0)
					{
						ulong mask = buf[0];
						bool s = shiftIndex != uint.MaxValue && (mask & (1UL << (int)shiftIndex)) != 0;
						bool g = mod5Index != uint.MaxValue && (mask & (1UL << (int)mod5Index)) != 0;
						return (s, g);
					}
				}

				return level switch
				{
					0 => (false, false),
					1 => (true, false),
					2 => (false, true),
					3 => (true, true),
					_ => (false, false)
				};
			}

			private static bool LevelHasKeysym(IntPtr map, uint key, uint layout, uint level, uint target)
			{
				IntPtr symsPtr;
				int count = xkb_keymap_key_get_syms_by_level(map, key, layout, level, out symsPtr);

				if (count <= 0 || symsPtr == IntPtr.Zero)
					return false;

				unsafe
				{
					var syms = (uint*)symsPtr;

					for (int i = 0; i < count; i++)
					{
						if (syms[i] == target)
							return true;
					}
				}

				return false;
			}

			private static string xkb_keymap_key_get_name_safe(IntPtr map, uint key)
			{
				var ptr = xkb_keymap_key_get_name(map, key);
				return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
			}

			private static uint KeysymFromRune(Rune rune)
			{
				uint cp = (uint)rune.Value;

				if (cp <= 0x7F)
					return cp;

				if (cp >= 0x00A0 && cp <= 0x00FF)
					return cp;

				if (cp >= 0x0100 && cp <= 0x10FFFF)
					return 0x01000000u | cp;

				return 0;
			}

			private static IntPtr AllocUtf8(string text, List<IntPtr> pins)
			{
				var bytes = Encoding.UTF8.GetBytes(text + "\0");
				var mem = Marshal.AllocHGlobal(bytes.Length);
				Marshal.Copy(bytes, 0, mem, bytes.Length);
				pins.Add(mem);
				return mem;
			}

			private void ResetState()
			{
				ResetNativeHandles();
				cache.Clear();
				ready = false;
				initTried = false;
				minKeycode = 0;
				maxKeycode = 0;
				shiftIndex = uint.MaxValue;
				mod5Index = uint.MaxValue;
				hasModsForLevel = false;
			}

			private void ResetNativeHandles()
			{
				if (keymap != IntPtr.Zero)
				{
					xkb_keymap_unref(keymap);
					keymap = IntPtr.Zero;
				}

				if (ctx != IntPtr.Zero)
				{
					xkb_context_unref(ctx);
					ctx = IntPtr.Zero;
				}
			}

			private const string LibXkbCommon = "libxkbcommon.so.0";

			[StructLayout(LayoutKind.Sequential)]
			private struct xkb_rule_names
			{
				public IntPtr rules;
				public IntPtr model;
				public IntPtr layout;
				public IntPtr variant;
				public IntPtr options;
			}

			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_context_new(uint flags);
			[DllImport(LibXkbCommon)] private static extern void xkb_context_unref(IntPtr ctx);
			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_keymap_new_from_names(IntPtr ctx, ref xkb_rule_names names, uint flags);
			[DllImport(LibXkbCommon)] private static extern void xkb_keymap_unref(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern int xkb_keymap_min_keycode(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern int xkb_keymap_max_keycode(IntPtr keymap);
			[DllImport(LibXkbCommon)] private static extern uint xkb_keymap_mod_get_index(IntPtr keymap, string name);
			[DllImport(LibXkbCommon)] private static extern int xkb_keymap_num_levels_for_key(IntPtr keymap, uint key, uint layout);
			[DllImport(LibXkbCommon)] private static extern int xkb_keymap_key_get_syms_by_level(IntPtr keymap, uint key, uint layout, uint level, out IntPtr symsOut);
			[DllImport(LibXkbCommon, EntryPoint = "xkb_keymap_key_get_mods_for_level")]
			private static extern int xkb_keymap_key_get_mods_for_level(IntPtr keymap, uint key, uint layout, uint level, IntPtr masksOut, UIntPtr masksOutSize);
			[DllImport(LibXkbCommon)] private static extern IntPtr xkb_keymap_key_get_name(IntPtr keymap, uint key);
		}
	}
}
#endif
