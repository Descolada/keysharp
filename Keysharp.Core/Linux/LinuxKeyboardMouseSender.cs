#if LINUX
using System.Text;
using System.Linq;
using SharpHook;
using SharpHook.Native;
using SharpHook.Data;
using static Keysharp.Core.Linux.SharpHookKeyMapper;
using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Linux
{
	/// <summary>
	/// Concrete implementation of KeyboardMouseSender for the linux platform.
	/// </summary>
	internal partial class LinuxKeyboardMouseSender : Common.Keyboard.KeyboardMouseSender
	{
		internal IEventSimulator sim => backend.sim;
		private readonly SharpHookKeySimulationBackend backend = new();
		private IEventSimulationSequenceBuilder eventBuilder;
		private int eventCount;

		internal LinuxKeyboardMouseSender()
		{
		}

		internal enum SendInstructionType
		{
			KeyDown,
			KeyUp,
			KeyStroke, // down+up
			Text,      // SimulateTextEntry / sequence AddText (mapped smartly later)
			MouseClick,
			MouseWheel
		}

		internal enum LogicalModifier
		{
			Ctrl,
			Shift,
			Alt,
			Win
		}

		// Prefer Right-Alt as AltGr on Linux (prevents menu activation better than neutral Alt).
		private const uint VK_ALTGR = VK_RMENU;

		internal readonly struct SendInstruction
		{
			public SendInstructionType Type { get; }
			// Key / Mouse button VK (VK_LBUTTON / VK_RBUTTON / etc.) for Key* or MouseClick.
			public uint Vk { get; }

			// Text payload for Text.
			public string? Text { get; }

			// Repeat count for KeyStroke or MouseClick.
			public long RepeatCount { get; }

			// For Key* (modifiers) bookkeeping.
			public bool IsModifier { get; }
			public LogicalModifier Modifier { get; }

			// Mouse click details:
			public int X { get; }
			public int Y { get; }
			public bool MoveOffset { get; }
			public KeyEventTypes MouseEventType { get; }

			// Mouse wheel details:
			public MouseWheelScrollDirection WheelDirection { get; }
			public short WheelAmount { get; }

			// Key / modifier constructor
			public SendInstruction(
				SendInstructionType type,
				uint vk,
				long repeatCount = 1,
				bool isModifier = false,
				LogicalModifier modifier = default)
			{
				Type = type;
				Vk = vk;
				Text = null;
				RepeatCount = repeatCount;
				IsModifier = isModifier;
				Modifier = modifier;

				X = CoordUnspecified;
				Y = CoordUnspecified;
				MoveOffset = false;
				MouseEventType = KeyEventTypes.KeyDownAndUp;

				WheelDirection = default;
				WheelAmount = 1;
			}

			// Text constructor
			public SendInstruction(string text)
			{
				Type = SendInstructionType.Text;
				Vk = 0;
				Text = text;
				RepeatCount = 1;
				IsModifier = false;
				Modifier = default;

				X = CoordUnspecified;
				Y = CoordUnspecified;
				MoveOffset = false;
				MouseEventType = KeyEventTypes.KeyDownAndUp;

				WheelDirection = default;
				WheelAmount = 1;
			}

			// Mouse click constructor
			public SendInstruction(uint vk, long count, int x, int y, bool moveOffset, KeyEventTypes evtType)
			{
				Type = SendInstructionType.MouseClick;
				Vk = vk;
				Text = null;
				RepeatCount = count < 0 ? 1 : count;
				IsModifier = false;
				Modifier = default;

				X = x;
				Y = y;
				MoveOffset = moveOffset;
				MouseEventType = evtType;

				WheelDirection = default;
				WheelAmount = 1;
			}

			// Mouse wheel constructor
			public SendInstruction(MouseWheelScrollDirection dir, short amount)
			{
				Type = SendInstructionType.MouseWheel;
				Vk = 0;
				Text = null;
				RepeatCount = 1;
				IsModifier = false;
				Modifier = default;

				X = CoordUnspecified;
				Y = CoordUnspecified;
				MoveOffset = false;
				MouseEventType = KeyEventTypes.KeyDownAndUp;

				WheelDirection = dir;
				WheelAmount = amount == 0 ? (short)1 : amount;
			}

			internal bool RequiresMouseMove(out int outX, out int outY)
			{
				if (X != int.MinValue || Y != int.MinValue)
				{
					outX = X; outY = Y;
					if (outX == int.MinValue || Y == int.MinValue)
					{
						var pos = System.Windows.Forms.Cursor.Position;
						if (outX == int.MinValue) outX = pos.X;
						if (outY == int.MinValue) outY = pos.Y;
					}
					return true;
				}
				outX = int.MinValue; outY = int.MinValue;
				return false;
			}
		}

		internal sealed class SendParseContext
		{
			public readonly List<SendInstruction> Instructions = new();
			public bool InBlindMode;
			public readonly HashSet<LogicalModifier> HeldBySend = new();
		}

		internal static class SendParser
		{
			private static uint ParseBlindExcludes(string token)
			{
				// Syntax similar to Windows: {Blind}, {Blind<^>^}, etc.
				uint excludes = 0;
				bool wantLeft = true, wantRight = true;

				for (int i = "Blind".Length; i < token.Length; i++)
				{
					var c = token[i];
					switch (c)
					{
						case '<': wantLeft = true; wantRight = false; break;
						case '>': wantLeft = false; wantRight = true; break;
						case '^':
							if (wantLeft) excludes |= MOD_LCONTROL;
							if (wantRight) excludes |= MOD_RCONTROL;
							wantLeft = wantRight = true;
							break;
						case '+':
							if (wantLeft) excludes |= MOD_LSHIFT;
							if (wantRight) excludes |= MOD_RSHIFT;
							wantLeft = wantRight = true;
							break;
						case '!':
							if (wantLeft) excludes |= MOD_LALT;
							if (wantRight) excludes |= MOD_RALT;
							wantLeft = wantRight = true;
							break;
						case '#':
							if (wantLeft) excludes |= MOD_LWIN;
							if (wantRight) excludes |= MOD_RWIN;
							wantLeft = wantRight = true;
							break;
						default:
							wantLeft = wantRight = true;
							break;
					}
				}
				return excludes;
			}

			public static void ParseBasic(
				string keys,
				SendRawModes sendRaw,
				SendParseContext ctx)
			{
				// Parse once; coalesce contiguous plain text here to reduce work in Input mode.
				var sbText = new StringBuilder();

				void FlushText()
				{
					if (sbText.Length > 0)
					{
						ctx.Instructions.Add(new SendInstruction(sbText.ToString()));
						sbText.Clear();
					}
				}

				var span = keys.AsSpan();
				var i = 0;

				while (i < span.Length)
				{
					var ch = span[i];

					if (sendRaw == SendRawModes.NotRaw)
					{
						// Brace token?
						if (ch == '{')
						{
							if (TryReadBraceToken(span, i, out var token, out var after))
							{
								i = after;

								// Handle control modes first
								if (token.StartsWith("Blind", StringComparison.OrdinalIgnoreCase))
								{
									FlushText();
									ctx.InBlindMode = true;
									continue;
								}
								if (token.Equals("Raw", StringComparison.OrdinalIgnoreCase)
								 || token.Equals("Text", StringComparison.OrdinalIgnoreCase))
								{
									// Switch to raw remainder: send exactly as-is (including braces)
									FlushText();
									if (i < span.Length)
										ctx.Instructions.Add(new SendInstruction(span.Slice(i).ToString()));
									return;
								}

								// Literal special characters inside braces: {!} {#} {+} {^} {{} {}}
								if (TryParseBraceLiteral(token, out var literal))
								{
									sbText.Append(literal);
									continue;
								}

								// Mouse: {Click ...}
								if (TryParseClickOptions(token, out var x, out var y, out var clickVk, out var evtType, out var clickCount, out var moveRel))
								{
									FlushText();
									ctx.Instructions.Add(new SendInstruction(clickVk, clickCount, x, y, moveRel, evtType));
									continue;
								}

								// Mouse buttons: {LButton [N|down|up]} etc.
								if (TryParseMouseButtonTokenWithExtras(token, out var mbVk, out var mbCount, out var mbType))
								{
									FlushText();
									ctx.Instructions.Add(new SendInstruction(mbVk, mbCount, CoordUnspecified, CoordUnspecified, false, mbType));
									continue;
								}

								// Mouse wheel tokens: {WheelUp}, {WheelDown}, {WheelLeft}, {WheelRight} [amount]
								if (TryParseWheelToken(token, out var dir, out var amount))
								{
									FlushText();
									ctx.Instructions.Add(new SendInstruction(dir, amount));
									continue;
								}

								// Unicode / ASC
								if (TryParseUnicodeToken(token, out var uniText))
								{
									sbText.Append(uniText);
									continue;
								}
								if (TryParseAscToken(token, out var ascText))
								{
									sbText.Append(ascText);
									continue;
								}

								// vk/sc tokens (with optional down/up and count)
								if (TryParseVkScToken(token, out var vk, out var type, out var count))
								{
									FlushText();
									if (count < 1) count = 1;
									if (type == KeyEventTypes.KeyDownAndUp)
									{
										ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, vk, count));
									}
									else
									{
										for (var r = 0; r < count; r++)
											ctx.Instructions.Add(new SendInstruction(type == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, vk));
									}
									continue;
								}

								// Single-letter / digit inside braces (e.g. {S 30}, {a down})
								if (TryParseSimpleBraceKey(token, out var svk, out var stype, out var scount))
								{
									FlushText();
									if (stype == KeyEventTypes.KeyDownAndUp)
										ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, svk, scount));
									else
										for (var r = 0; r < scount; r++)
											ctx.Instructions.Add(new SendInstruction(stype == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, svk));
									continue;
								}

								// Named special keys, with optional repeat/action: {Enter 2}, {Tab}, {Left down}, ...
								if (TryParseSpecialKeyTokenWithExtras(token, out var specialVk, out var specialCount, out var specialType))
								{
									FlushText();
									if (specialType == KeyEventTypes.KeyDownAndUp)
										ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, specialVk, specialCount));
									else
										for (var r = 0; r < specialCount; r++)
											ctx.Instructions.Add(new SendInstruction(specialType == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, specialVk));
									continue;
								}

								// Unknown -> send literally "{token}"
								sbText.Append('{').Append(token).Append('}');
								continue;
							}
							else
							{
								// Malformed: treat '{' literally
								sbText.Append('{');
								i++;
								continue;
							}
						}

						// Prefix modifiers ^ + ! #
						if (IsModifierPrefixChar(ch))
						{
							var nextIndex = i + 1;
							if (nextIndex < span.Length)
							{
								var mod = PrefixCharToModifier(ch);
								var mvk = ModifierToVk(mod);

								// If next is a brace-token, wrap the whole token with mod down/up
								if (span[nextIndex] == '{' && TryReadBraceToken(span, nextIndex, out var tok, out var after))
								{
									FlushText();

									if (mvk != 0)
										ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyDown, mvk, 1, isModifier: true, modifier: mod));

									// Re-parse tok as if it appeared standalone
									if (TryParseClickOptions(tok, out var x, out var y, out var clickVk, out var evtType, out var clickCount, out var moveRel))
										ctx.Instructions.Add(new SendInstruction(clickVk, clickCount, x, y, moveRel, evtType));
									else if (TryParseMouseButtonTokenWithExtras(tok, out var mbVk, out var mbCount, out var mbType))
										ctx.Instructions.Add(new SendInstruction(mbVk, mbCount, CoordUnspecified, CoordUnspecified, false, mbType));
									else if (TryParseWheelToken(tok, out var dir, out var amount))
										ctx.Instructions.Add(new SendInstruction(dir, amount));
									else if (TryParseUnicodeToken(tok, out var uni))
										ctx.Instructions.Add(new SendInstruction(uni));
									else if (TryParseAscToken(tok, out var asc))
										ctx.Instructions.Add(new SendInstruction(asc));
									else if (TryParseVkScToken(tok, out var pvk, out var ptype, out var pcount))
									{
										if (ptype == KeyEventTypes.KeyDownAndUp)
											ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, pvk, pcount));
										else
											for (var r = 0; r < pcount; r++)
												ctx.Instructions.Add(new SendInstruction(ptype == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, pvk));
									}
									else if (TryParseSimpleBraceKey(tok, out var svk, out var stype, out var scount))
									{
										if (stype == KeyEventTypes.KeyDownAndUp)
											ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, svk, scount));
										else
											for (var r = 0; r < scount; r++)
												ctx.Instructions.Add(new SendInstruction(stype == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, svk));
									}
									else if (TryParseSpecialKeyTokenWithExtras(tok, out var sv, out var sc, out var stype2))
									{
										if (stype == KeyEventTypes.KeyDownAndUp)
											ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyStroke, sv, sc));
										else
											for (var r = 0; r < sc; r++)
												ctx.Instructions.Add(new SendInstruction(stype2 == KeyEventTypes.KeyDown ? SendInstructionType.KeyDown : SendInstructionType.KeyUp, sv));
									}
									else if (TryParseBraceLiteral(tok, out var lit))
									{
										ctx.Instructions.Add(new SendInstruction(lit.ToString()));
									}
									else
									{
										// unknown -> literal
										ctx.Instructions.Add(new SendInstruction("{" + tok + "}"));
									}

									if (mvk != 0)
										ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyUp, mvk, 1, isModifier: true, modifier: mod));

									i = after;
									continue;
								}

								// Next is a character: apply modifier only to that char
								FlushText();
								if (mvk != 0)
									ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyDown, mvk, 1, isModifier: true, modifier: mod));

								AddPlainChar(span[nextIndex], sendRaw, ctx); // will enqueue as Text-one-char

								if (mvk != 0)
									ctx.Instructions.Add(new SendInstruction(SendInstructionType.KeyUp, mvk, 1, isModifier: true, modifier: mod));

								i = nextIndex + 1;
								continue;
							}
						}
					}

					// Plain char path:
					if (sendRaw == SendRawModes.NotRaw)
						sbText.Append(ch);
					else
						// Raw/Text mode: literal, including braces (this loop only enters in case we got here before {Raw} remainder)
						sbText.Append(ch);
					i++;
				}

				FlushText();
			}

			private static void AddPlainChar(char ch, SendRawModes sendRaw, SendParseContext ctx)
			{
				// Always emit as text; executor does smart mapping later
				ctx.Instructions.Add(new SendInstruction(ch.ToString()));
			}

			private static bool IsModifierPrefixChar(char ch)
				=> ch == '^' || ch == '+' || ch == '!' || ch == '#';

			private static LogicalModifier PrefixCharToModifier(char ch) => ch switch
			{
				'^' => LogicalModifier.Ctrl,
				'+' => LogicalModifier.Shift,
				'!' => LogicalModifier.Alt,
				'#' => LogicalModifier.Win,
				_ => default
			};

			private static uint ModifierToVk(LogicalModifier mod) => mod switch
			{
				LogicalModifier.Ctrl => VK_CONTROL,
				LogicalModifier.Shift => VK_SHIFT,
				LogicalModifier.Alt => VK_MENU,
				LogicalModifier.Win => VK_LWIN,
				_ => 0
			};

			// ---------- Token readers ----------

			// Extract {...} token starting at index
			private static bool TryReadBraceToken(ReadOnlySpan<char> span, int braceIndex, out string token, out int newIndex)
			{
				token = string.Empty;
				newIndex = braceIndex;
				if (braceIndex >= span.Length || span[braceIndex] != '{')
					return false;

				var search = span.Slice(braceIndex + 1);
				var closeRel = search.IndexOf('}');
				if (closeRel < 0)
					return false;

				// Handle {}} specially: treat as literal '}' and consume both closing braces.
				if (closeRel == 0 && search.Length > 1 && search[1] == '}')
				{
					token = string.Empty;
					newIndex = braceIndex + 3; // "{}}" -> advance past all three chars
					return true;
				}

				// If first char is '}', this is an empty token (e.g. {}} or {}), meaning literal '}' or '{}/{}'
				token = closeRel == 0 ? string.Empty : search.Slice(0, closeRel).Trim().ToString();
				newIndex = braceIndex + closeRel + 2;
				return true;
			}

			// {!} {#} {+} {^} {{} {}}
			private static bool TryParseBraceLiteral(string token, out char literal)
			{
				literal = '\0';

				// Empty token (e.g. {}}) means literal '}'.
				if (token.Length == 0)
				{
					literal = '}';
					return true;
				}

				if (token.Length == 1)
				{
					switch (token[0])
					{
						case '!':
						case '#':
						case '+':
						case '^':
						case '{':
						case '}':
							literal = token[0]; return true;
					}
				}

				return false;
			}

			// {U+nnnn} [count]
			private static bool TryParseUnicodeToken(string token, out string text)
			{
				text = string.Empty;
				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
					return false;

				if (!parts[0].StartsWith("U+", StringComparison.OrdinalIgnoreCase))
					return false;

				if (!int.TryParse(parts[0].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var cp))
					return false;

				if (cp < 0 || cp > 0x10FFFF)
					return false;

				int count = 1;
				if (parts.Length >= 2 && int.TryParse(parts[1], out var c) && c > 0) count = c;

				var s = char.ConvertFromUtf32(cp);
				var sb = new StringBuilder(s.Length * count);
				for (int i = 0; i < count; i++) sb.Append(s);
				text = sb.ToString();
				return true;
			}

			// {Asc nnnnn}  -> on Linux, treat as Unicode code point 'nnnnn'
			private static bool TryParseAscToken(string token, out string text)
			{
				text = string.Empty;
				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) return false;
				if (!parts[0].Equals("Asc", StringComparison.OrdinalIgnoreCase)) return false;
				if (parts.Length < 2 || !int.TryParse(parts[1], out var n)) return false;
				if (n < 0 || n > 0x10FFFF) return false;
				text = char.ConvertFromUtf32(n);
				return true;
			}

			// {vkXX}, {scYYY}, {vkXXscYYY} [N] [down|up]
			private static bool TryParseVkScToken(string token, out uint vk, out KeyEventTypes type, out int count)
			{
				vk = 0; type = KeyEventTypes.KeyDownAndUp; count = 1;

				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) return false;

				// First part may be composite like "vk41sc01E"
				string p0 = parts[0];

				int idxVk = p0.IndexOf("vk", StringComparison.OrdinalIgnoreCase);
				if (idxVk >= 0 && idxVk + 2 <= p0.Length)
				{
					int start = idxVk + 2;
					int len = 0;
					while (start + len < p0.Length && IsHex(p0[start + len]) && len < 4) len++;
					if (len > 0 && uint.TryParse(p0.Substring(start, len), System.Globalization.NumberStyles.HexNumber, null, out var vkVal))
						vk = vkVal;
				}
				// sc is currently ignored (SharpHook works with vk mapping); parsed but unused.

				// Other parts: optional count and/or 'down'/'up'
				for (int i = 1; i < parts.Length; i++)
				{
					var s = parts[i];
					if (int.TryParse(s, out var n) && n > 0) { count = n; continue; }
					if (s.StartsWith("down", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyDown;
					else if (s.StartsWith("up", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyUp;
				}

				return vk != 0;
			}

			private static bool IsHex(char c)
				=> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

			private static bool TryParseClickOptions(string token, out int x, out int y, out uint vk, out KeyEventTypes eventType, out long repeatCount, out bool moveOffset)
			{
				x = CoordUnspecified;
				y = CoordUnspecified;
				vk = VK_LBUTTON;
				eventType = KeyEventTypes.KeyDownAndUp;
				repeatCount = 1L;
				moveOffset = false;
				var parts = token.Split(new[] { ' ', '\t' });
				if (parts.Length == 0) return false;
				if (!parts[0].Equals("Click", StringComparison.OrdinalIgnoreCase)) return false;
				HookThread.ParseClickOptions(token.AsSpan().Slice(5), ref x, ref y, ref vk, ref eventType, ref repeatCount, ref moveOffset);
				return true;
			}

			// {LButton [N|down|up]} etc.
			private static bool TryParseMouseButtonTokenWithExtras(string token, out uint vk, out long count, out KeyEventTypes type)
			{
				vk = 0; count = 1; type = KeyEventTypes.KeyDownAndUp;

				var parts = token.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) return false;

				switch (parts[0].ToLowerInvariant())
				{
					case "lbutton": vk = VK_LBUTTON; break;
					case "rbutton": vk = VK_RBUTTON; break;
					case "mbutton": vk = VK_MBUTTON; break;
					case "xbutton1": vk = VK_XBUTTON1; break;
					case "xbutton2": vk = VK_XBUTTON2; break;
					default: return false;
				}

				for (int i = 1; i < parts.Length; i++)
				{
					if (long.TryParse(parts[i], out var n) && n > 0) { count = n; continue; }
					if (parts[i].StartsWith("down", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyDown;
					else if (parts[i].StartsWith("up", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyUp;
				}
				return true;
			}

			// {WheelUp [amt]} etc.  (amt defaults to 1 notch)
			private static bool TryParseWheelToken(string token, out MouseWheelScrollDirection dir, out short amount)
			{
				dir = default; amount = 1;
				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) return false;
				switch (parts[0].ToLowerInvariant())
				{
					case "wheelup": dir = MouseWheelScrollDirection.Vertical; amount = 1; return true;
					case "wheeldown": dir = MouseWheelScrollDirection.Vertical; amount = -1; return true; // negative notch
					case "wheelleft": dir = MouseWheelScrollDirection.Horizontal; amount = -1; return true;
					case "wheelright": dir = MouseWheelScrollDirection.Horizontal; amount = 1; return true;
				}

				// Also allow "{WheelUp 3}" style
				if (parts[0].StartsWith("wheel", StringComparison.OrdinalIgnoreCase))
				{
					var name = parts[0].ToLowerInvariant();
					if (name == "wheelup" || name == "wheeldown" || name == "wheelleft" || name == "wheelright")
					{
						ushort amt = 1;
						if (parts.Length >= 2 && ushort.TryParse(parts[1], out var parsed) && parsed > 0)
							amt = parsed;

						switch (name)
						{
							case "wheelup": dir = MouseWheelScrollDirection.Vertical; amount = (short)amt; return true;
							case "wheeldown": dir = MouseWheelScrollDirection.Vertical; amount = (short)-amt; return true;
							case "wheelleft": dir = MouseWheelScrollDirection.Horizontal; amount = (short)amt; return true;
							case "wheelright": dir = MouseWheelScrollDirection.Horizontal; amount = (short)-amt; return true;
						}
					}
				}
				return false;
			}

			// Single-letter/digit/punct key inside braces: {S 30}, {a down}, {5}, {= up}
			private static bool TryParseSimpleBraceKey(ReadOnlySpan<char> token, out uint vk, out KeyEventTypes type, out int count)
			{
				vk = 0; type = KeyEventTypes.KeyDownAndUp; count = 1;

				var parts = token.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) return false;

				// Accept one visible ASCII char (not { } as those are handled as brace-literal)
				if (parts[0].Length != 1)
					return false;

				var ch = parts[0][0];

				// Map to a VK (letters uppercase)
				if (ch is >= 'a' and <= 'z')
					vk = (uint)char.ToUpperInvariant(ch);
				else if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
					vk = (uint)ch;
				else
					vk = CharToVk(ch);

				if (vk == 0)
					return false;

				for (int i = 1; i < parts.Length; i++)
				{
					if (int.TryParse(parts[i], out var n) && n > 0) { count = n; continue; }
					if (parts[i].StartsWith("down", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyDown;
					else if (parts[i].StartsWith("up", StringComparison.OrdinalIgnoreCase)) type = KeyEventTypes.KeyUp;
				}
				return true;
			}

			private static uint CharToVk(char ch)
			{
				if (ch is >= 'A' and <= 'Z') return (uint)ch;
				if (ch is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(ch);
				if (ch is >= '0' and <= '9') return (uint)ch;

				return ch switch
				{
					' ' => VK_SPACE,
					'\t' => VK_TAB,
					'\r' => VK_RETURN,
					'\n' => VK_RETURN,
					'\b' => VK_BACK,
					'-' => VK_OEM_MINUS,
					'_' => VK_OEM_MINUS, // with Shift
					'=' => VK_OEM_PLUS,
					'+' => VK_OEM_PLUS,  // with Shift
					'[' => VK_OEM_4,
					'{' => VK_OEM_4,     // with Shift
					']' => VK_OEM_6,
					'}' => VK_OEM_6,     // with Shift
					'\\' => VK_OEM_5,
					'|' => VK_OEM_5,     // with Shift
					';' => VK_OEM_1,
					':' => VK_OEM_1,     // with Shift
					'\'' => VK_OEM_7,
					'"' => VK_OEM_7,     // with Shift
					',' => VK_OEM_COMMA,
					'<' => VK_OEM_COMMA, // with Shift
					'.' => VK_OEM_PERIOD,
					'>' => VK_OEM_PERIOD, // with Shift
					'/' => VK_OEM_2,
					'?' => VK_OEM_2,     // with Shift
					'`' => VK_OEM_3,
					'~' => VK_OEM_3,     // with Shift
					_ => 0
				};
			}

			private static bool TryParseModifierToken(
				string token,
				out LogicalModifier modifier,
				out KeyEventTypes type)
			{
				modifier = default;
				type = KeyEventTypes.KeyDownAndUp;

				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
					return false;

				var name = parts[0].ToLowerInvariant();
				switch (name)
				{
					case "ctrl":
					case "control":
						modifier = LogicalModifier.Ctrl; break;
					case "shift":
						modifier = LogicalModifier.Shift; break;
					case "alt":
						modifier = LogicalModifier.Alt; break;
					case "win":
					case "lwin":
					case "rwin":
						modifier = LogicalModifier.Win; break;
					default:
						return false;
				}

				if (parts.Length >= 2)
				{
					var action = parts[1].ToLowerInvariant();
					if (action == "down")
						type = KeyEventTypes.KeyDown;
					else if (action == "up")
						type = KeyEventTypes.KeyUp;
				}
				return true;
			}

			private static bool TryParseSpecialKeyTokenWithExtras(
				string token,
				out uint vk,
				out long count,
				out KeyEventTypes type)
			{
				vk = 0;
				count = 1;
				type = KeyEventTypes.KeyDownAndUp;

				var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
					return false;

				if (!TryParseSpecialKeyToken(parts[0], out vk))
					return false;

				for (int i = 1; i < parts.Length; i++)
				{
					if (long.TryParse(parts[i], out var parsed) && parsed > 0)
					{
						count = parsed;
					}
					else if (parts[i].StartsWith("down", StringComparison.OrdinalIgnoreCase))
					{
						type = KeyEventTypes.KeyDown;
					}
					else if (parts[i].StartsWith("up", StringComparison.OrdinalIgnoreCase))
					{
						type = KeyEventTypes.KeyUp;
					}
				}

				return true;
			}

			private static bool TryParseSpecialKeyToken(string token, out uint vk)
			{
				vk = 0;
				var t = token.ToLowerInvariant();

				switch (t)
				{
					case "enter": vk = VK_RETURN; return true;
					case "tab": vk = VK_TAB; return true;
					case "esc":
					case "escape": vk = VK_ESCAPE; return true;
					case "bs":
					case "backspace": vk = VK_BACK; return true;

					case "delete":
					case "del": vk = VK_DELETE; return true;
					case "insert":
					case "ins": vk = VK_INSERT; return true;

					case "home": vk = VK_HOME; return true;
					case "end": vk = VK_END; return true;
					case "pgup": vk = VK_PRIOR; return true;
					case "pgdn": vk = VK_NEXT; return true;
					case "space": vk = VK_SPACE; return true;

					case "up": vk = VK_UP; return true;
					case "down": vk = VK_DOWN; return true;
					case "left": vk = VK_LEFT; return true;
					case "right": vk = VK_RIGHT; return true;

					// Numpad (NumLock ON behavior)
					case "numpad0": vk = VK_NUMPAD0; return true;
					case "numpad1": vk = VK_NUMPAD1; return true;
					case "numpad2": vk = VK_NUMPAD2; return true;
					case "numpad3": vk = VK_NUMPAD3; return true;
					case "numpad4": vk = VK_NUMPAD4; return true;
					case "numpad5": vk = VK_NUMPAD5; return true;
					case "numpad6": vk = VK_NUMPAD6; return true;
					case "numpad7": vk = VK_NUMPAD7; return true;
					case "numpad8": vk = VK_NUMPAD8; return true;
					case "numpad9": vk = VK_NUMPAD9; return true;
					case "numpaddot": vk = VK_DECIMAL; return true;
					case "numpadenter": vk = VK_RETURN; return true;
					case "numpadmult": vk = VK_MULTIPLY; return true;
					case "numpaddiv": vk = VK_DIVIDE; return true;
					case "numpadadd": vk = VK_ADD; return true;
					case "numpadsub": vk = VK_SUBTRACT; return true;
				}

				// F-keys: F1..F24
				if (t.Length >= 2 && (t[0] == 'f' || t[0] == 'F') && int.TryParse(t.AsSpan(1), out var fn))
				{
					if (fn is >= 1 and <= 24)
					{
						vk = VK_F1 + (uint)(fn - 1);
						return true;
					}
				}

				return false;
			}
		}

		internal static class SendExecutor
		{
			public static void Execute(
				SendParseContext ctx,
				SendModes mode,
				LinuxKeyboardMouseSender self,
				SharpHookKeySimulationBackend backend)
			{
				var lht = Script.TheScript.HookThread as LinuxHookThread;
				bool textOnly = ctx.Instructions.All(i => i.Type == SendInstructionType.Text);
				lht?.BeginSend();
				lht?.BeginInjectedIgnore();
				// Ensure the hotkey suffix is logically released so grabs don’t swallow injected keystrokes.
				if (lht != null && lht.ActiveHotkeyVk != 0)
					lht.ForceReleaseEndKeyX11(lht.ActiveHotkeyVk);
				LinuxHookThread.GrabSnapshot? grabSnapshot = lht?.BeginSendUngrab();
				if (mode == SendModes.InputThenPlay) mode = SendModes.Input;
				else if (mode == SendModes.Play) mode = SendModes.Event;

				var modsInitial = lht != null ? lht.CurrentModifiersLR() : self.GetModifierLRState(true);
				Console.WriteLine($"[Send] modsInitial={modsInitial:X}");
				var capsOn = Script.TheScript.HookThread.IsKeyToggledOn(VK_CAPITAL);
				// Preserve held modifiers when in {Blind} mode so wildcard hotkeys propagate them.
				var modsDuring = ctx.InBlindMode ? modsInitial : 0u;
				// When sending text-only in Input mode, avoid modifier adjustments to reduce duplicate/resend noise.
				var adjustMods = (modsInitial != modsDuring || capsOn);
				Console.WriteLine($"[Send] adjustMods={adjustMods} textOnly={textOnly} mode={mode} capsOn={capsOn} modsDuring={modsDuring:X}");

				if (adjustMods)
					Console.WriteLine($"[Send] Adjust mods from {modsInitial:X} to {modsDuring:X} capsOn={capsOn}");
				if (adjustMods)
					self.SetModifierLRState(modsDuring, modsInitial, 0, false, true, KeyboardMouseSender.KeyIgnore);

				bool IsModifierVk(uint vk) =>
					vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_LCONTROL || vk == VK_RCONTROL
					|| vk == VK_LMENU || vk == VK_RMENU || vk == VK_LWIN || vk == VK_RWIN;

				var keyDelay = ThreadAccessors.A_KeyDelay;
				var keyDuration = ThreadAccessors.A_KeyDuration;

				try
				{
					if (mode == SendModes.Input)
					{
						Console.WriteLine("[Send] ExecuteAsInput");
						ExecuteAsInput(ctx.Instructions, self, backend, lht, modsInitial);
						return;
					}

					Console.WriteLine("[Send] ExecuteAsEvent");
					ExecuteAsEvent(ctx.Instructions, self, keyDelay, keyDuration);
				}
				finally
				{
					if (adjustMods)
						self.SetModifierLRState(modsInitial, self.GetModifierLRState(), 0, false, true, KeyboardMouseSender.KeyIgnore);
					if (grabSnapshot.HasValue)
					lht?.EndSendUngrab(grabSnapshot.Value);
					// Restore physical modifier tracking to what it was before the send so held prefixes remain.
					if (lht != null)
						lht.MarkModifiersDown(modsInitial);
					lht?.EndSend();
					lht?.EndInjectedIgnore();
				}
			}
			private static void ExecuteAsInput(
				List<SendInstruction> instructions,
				LinuxKeyboardMouseSender self,
				SharpHookKeySimulationBackend backend,
				LinuxHookThread? lht,
				uint modsInitial)
			{
				IKeySimulationSequence? seq = null;
				int seqId = Environment.TickCount;
				HashSet<uint> injectedHeld = new();

				// Keep these held across a run of mapped characters to minimize toggles
				// Start with no modifiers held; we handle presses per character and restore afterward.
				bool shiftPhys = false;
				bool altGrPhys = false;
				bool heldShift = false, heldAltGr = false;
				bool shiftSimulatedDown = false, altGrSimulatedDown = false;

				void EnsureSeq() => seq ??= backend.BeginSequence();

				void SeqDown(uint vk)
				{
					if (lht != null && injectedHeld.Add(vk))
						lht.TrackInjectedHold(vk, true);
					Console.WriteLine($"[SendSeq {seqId}] KeyDown vk={vk}");
					EnsureSeq();
					seq!.AddKeyDown(vk);
				}
				void SeqUp(uint vk)
				{
					// Keep injectedHold until the hook sees the physical event.
					Console.WriteLine($"[SendSeq {seqId}] KeyUp vk={vk}");
					EnsureSeq();
					seq!.AddKeyUp(vk);
				}
				void SeqStroke(uint vk)
				{
					// Expand to down/up so injected holds get tracked and filtered by the hook.
					SeqDown(vk);
					SeqUp(vk);
				}

				void ReleaseHeldTextMods(Action ensure)
				{
					if (shiftSimulatedDown)
					{
						ensure();
						SeqUp(VK_SHIFT);
						heldShift = false;
						shiftSimulatedDown = false;
					}
					if (altGrSimulatedDown)
					{
						ensure();
						SeqUp(VK_ALTGR);
						heldAltGr = false;
						altGrSimulatedDown = false;
					}
				}

				void FlushSeq()
				{
					if (seq != null)
					{
						ReleaseHeldTextMods(EnsureSeq);
						Console.WriteLine($"[SendInputTrace] Commit sequence id={seqId}");
						seq.Commit();
						seq = null;
					}
				}

				void PressTextMods(bool needShift, bool needAltGr)
				{
					if (needAltGr && !heldAltGr && !altGrPhys) { SeqDown(VK_ALTGR); heldAltGr = true; altGrSimulatedDown = true; }
					if (needShift && !heldShift) { SeqDown(VK_SHIFT); heldShift = true; shiftSimulatedDown = true; }
					if (!needShift && shiftSimulatedDown) { SeqUp(VK_SHIFT); heldShift = shiftPhys; shiftSimulatedDown = false; }
					if (!needAltGr && altGrSimulatedDown) { SeqUp(VK_ALTGR); heldAltGr = altGrPhys; altGrSimulatedDown = false; }
				}

				// Coalesce contiguous Text instructions in Input mode
				var textBatch = new StringBuilder();

				bool KeyIsHeld(uint vk) => lht != null && lht.IsKeyDown(vk);

				void EmitSmartText(string text)
				{
					if (string.IsNullOrEmpty(text))
						return;

					var fb = new StringBuilder();
					bool lastWasCR = false;

					void FlushFallback()
					{
						if (fb.Length > 0)
						{
							Console.WriteLine($"[SendInputTrace] Fallback text \"{fb}\" via SimulateTextEntry");
							FlushSeq(); // releases held text mods
							self.sim.SimulateTextEntry(fb.ToString());
							fb.Clear();
						}
					}

					foreach (var rune in text.EnumerateRunes())
					{
						int rv = rune.Value;

						// --- Control chars → real keystrokes ---
						if (rv == '\r')
						{
							FlushFallback();
							EnsureSeq();
							ReleaseHeldTextMods(EnsureSeq);
							SeqStroke(VK_RETURN);
							lastWasCR = true;
							continue;
						}
						if (rv == '\n')
						{
							if (lastWasCR) { lastWasCR = false; continue; } // collapse CRLF → one Enter
							FlushFallback();
							EnsureSeq();
							ReleaseHeldTextMods(EnsureSeq);
							SeqStroke(VK_RETURN);
							continue;
						}
						lastWasCR = false;

						if (rv == '\t')
						{
							FlushFallback();
							EnsureSeq();
							ReleaseHeldTextMods(EnsureSeq);
							SeqStroke(VK_TAB);
							continue;
						}
						if (rv == '\b')
						{
							FlushFallback();
							EnsureSeq();
							ReleaseHeldTextMods(EnsureSeq);
							SeqStroke(VK_BACK);
							continue;
						}

						// --- Printable / layout-aware mapping ---
						if (LinuxCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
							&& !KeyIsHeld(vk))
						{
							Console.WriteLine($"[SendInputTrace] Rune {rune} -> vk={vk} shift={needShift} altgr={needAltGr}");
							FlushFallback();
							EnsureSeq();
							PressTextMods(needShift, needAltGr);
							SeqStroke(vk);
						}
						else
						{
							// If the physical key is held (e.g., suffix), fall back to text entry to avoid conflicts.
							Console.WriteLine($"[SendInputTrace] Rune {rune} fallback to SimulateTextEntry");
							FlushSeq();
							fb.Append(rune.ToString());
						}
					}

					FlushSeq();      // releases held text mods
					FlushFallback(); // send any remaining fallback text
				}

				void FlushTextBatch()
				{
					if (textBatch.Length > 0)
					{
						EmitSmartText(textBatch.ToString());
						textBatch.Clear();
					}
				}

				foreach (var instr in instructions)
				{
					Console.WriteLine($"[SendInputTrace] Instr type={instr.Type} vk={instr.Vk} text=\"{instr.Text}\" repeat={instr.RepeatCount}");
					switch (instr.Type)
					{
						case SendInstructionType.Text:
							Console.WriteLine($"[SendInput] Text \"{instr.Text}\"");
							// Coalesce in Input mode
							textBatch.Append(instr.Text);
							break;

						case SendInstructionType.KeyDown:
							FlushTextBatch();
							Console.WriteLine($"[SendInput] KeyDown vk={instr.Vk}");
							SeqDown(instr.Vk);
							break;

						case SendInstructionType.KeyUp:
							FlushTextBatch();
							Console.WriteLine($"[SendInput] KeyUp vk={instr.Vk}");
							SeqUp(instr.Vk);
							break;

						case SendInstructionType.KeyStroke:
							FlushTextBatch();
							for (var i = 0L; i < instr.RepeatCount; i++)
							{
								Console.WriteLine($"[SendInput] KeyStroke vk={instr.Vk}");
								SeqDown(instr.Vk);
								SeqUp(instr.Vk);
							}
							break;

						case SendInstructionType.MouseClick:
							FlushTextBatch();
							FlushSeq();

							// Move-only if RepeatCount == 0 and coords specified
							if (instr.RepeatCount == 0 && (instr.X != CoordUnspecified || instr.Y != CoordUnspecified))
							{
								int mx = instr.X, my = instr.Y;
								self.MouseMove(ref mx, ref my, ref Unsafe.AsRef<uint>(ref Unsafe.NullRef<uint>()), 0, instr.MoveOffset);
								self.DoMouseDelay();
								break;
							}

							for (var i = 0L; i < instr.RepeatCount; i++)
							{
								self.MouseEvent(((uint)instr.MouseEventType << 16) | instr.Vk, 0,
									instr.X, instr.Y);
								self.DoMouseDelay();
							}
							break;

						case SendInstructionType.MouseWheel:
							FlushTextBatch();
							FlushSeq();
							{
								if (instr.RequiresMouseMove(out int finalX, out int finalY))
									self.sim.SimulateMouseMovement((short)finalX, (short)finalY);
								self.sim.SimulateMouseWheel(instr.WheelAmount, instr.WheelDirection);
								self.DoMouseDelay();
							}
							break;
					}
				}

				FlushTextBatch();
				FlushSeq();
			}

			private static void ExecuteAsEvent(
				List<SendInstruction> instructions,
				LinuxKeyboardMouseSender self,
				long keyDelay,
				long keyDuration)
			{
				Console.WriteLine("[SendEvent] Begin");
				var lht = Script.TheScript.HookThread as LinuxHookThread;
				var injectedHeld = new HashSet<uint>();

				void Press(uint vk)
				{
					Console.WriteLine($"[SendEvent] KeyStroke vk={vk}");
					self.backend.KeyDown(vk);
					if (lht != null && injectedHeld.Add(vk)) lht.TrackInjectedHold(vk, true);
					if (keyDuration >= 0) Flow.SleepWithoutInterruption(keyDuration);
					self.backend.KeyUp(vk);
					if (lht != null && injectedHeld.Remove(vk)) lht.TrackInjectedHold(vk, false);
				}

				bool heldShift = false, heldAltGr = false;

				void PressTextMods(bool needShift, bool needAltGr)
				{
					if (needAltGr && !heldAltGr)
					{
						self.backend.KeyDown(VK_ALTGR);
						if (lht != null && injectedHeld.Add(VK_ALTGR)) lht.TrackInjectedHold(VK_ALTGR, true);
						heldAltGr = true;
						if (keyDuration >= 0) Flow.SleepWithoutInterruption(keyDuration);
					}
					if (needShift && !heldShift)
					{
						self.backend.KeyDown(VK_SHIFT);
						if (lht != null && injectedHeld.Add(VK_SHIFT)) lht.TrackInjectedHold(VK_SHIFT, true);
						heldShift = true;
						if (keyDuration >= 0) Flow.SleepWithoutInterruption(keyDuration);
					}
					if (!needShift && heldShift)
					{
						self.backend.KeyUp(VK_SHIFT);
						if (lht != null && injectedHeld.Remove(VK_SHIFT)) lht.TrackInjectedHold(VK_SHIFT, false);
						heldShift = false;
						if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
					}
					if (!needAltGr && heldAltGr)
					{
						self.backend.KeyUp(VK_ALTGR);
						if (lht != null && injectedHeld.Remove(VK_ALTGR)) lht.TrackInjectedHold(VK_ALTGR, false);
						heldAltGr = false;
						if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
					}
				}

				void ReleaseHeldTextMods()
				{
					if (heldShift)
					{
						self.backend.KeyUp(VK_SHIFT);
						if (lht != null && injectedHeld.Remove(VK_SHIFT)) lht.TrackInjectedHold(VK_SHIFT, false);
						heldShift = false;
						if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
					}
					if (heldAltGr)
					{
						self.backend.KeyUp(VK_ALTGR);
						if (lht != null && injectedHeld.Remove(VK_ALTGR)) lht.TrackInjectedHold(VK_ALTGR, false);
						heldAltGr = false;
						if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
					}
				}

				// In Event mode: send each Text instruction separately (to preserve delays between them)
				void EmitSmartText(string text)
				{
					if (string.IsNullOrEmpty(text))
						return;

					var fb = new StringBuilder();
					bool lastWasCR = false;

					void FlushFallback()
					{
						if (fb.Length > 0)
						{
							ReleaseHeldTextMods();
							self.sim.SimulateTextEntry(fb.ToString());
							fb.Clear();
						}
					}

					void Press(uint vk)
					{
						self.backend.KeyDown(vk);
						if (keyDuration >= 0) Flow.SleepWithoutInterruption(keyDuration);
						self.backend.KeyUp(vk);
					}

					foreach (var rune in text.EnumerateRunes())
					{
						int rv = rune.Value;

						// --- Control chars → real keystrokes ---
						if (rv == '\r')
						{
							FlushFallback();
							ReleaseHeldTextMods();
							Press(VK_RETURN);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							lastWasCR = true;
							continue;
						}
						if (rv == '\n')
						{
							if (lastWasCR) { lastWasCR = false; continue; } // collapse CRLF
							FlushFallback();
							ReleaseHeldTextMods();
							Press(VK_RETURN);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							continue;
						}
						lastWasCR = false;

						if (rv == '\t')
						{
							FlushFallback();
							ReleaseHeldTextMods();
							Press(VK_TAB);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							continue;
						}
						if (rv == '\b')
						{
							FlushFallback();
							ReleaseHeldTextMods();
							Press(VK_BACK);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							continue;
						}

						// --- Printable / layout-aware mapping ---
						if (LinuxCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr))
						{
							FlushFallback();
							PressTextMods(needShift, needAltGr);
							Press(vk);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
						}
						else
						{
							fb.Append(rune.ToString());
						}
					}

					ReleaseHeldTextMods();
					FlushFallback();
				}

				foreach (var instr in instructions)
				{
					switch (instr.Type)
					{
						case SendInstructionType.KeyDown:
							Console.WriteLine($"[SendEvent] KeyDown vk={instr.Vk}");
							self.backend.KeyDown(instr.Vk);
							if (lht != null && injectedHeld.Add(instr.Vk)) lht.TrackInjectedHold(instr.Vk, true);
							if (keyDuration >= 0) Flow.SleepWithoutInterruption(keyDuration);
							break;

						case SendInstructionType.KeyUp:
							Console.WriteLine($"[SendEvent] KeyUp vk={instr.Vk}");
							self.backend.KeyUp(instr.Vk);
							if (lht != null && injectedHeld.Remove(instr.Vk)) lht.TrackInjectedHold(instr.Vk, false);
							if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							break;

						case SendInstructionType.KeyStroke:
							for (var i = 0L; i < instr.RepeatCount; i++)
							{
								Press(instr.Vk);
								if (keyDelay >= 0) Flow.SleepWithoutInterruption(keyDelay);
							}
							break;

						case SendInstructionType.Text:
							EmitSmartText(instr.Text ?? string.Empty);
							break;

						case SendInstructionType.MouseClick:
							// Move-only if RepeatCount == 0 and coords specified
							if (instr.RepeatCount == 0 && (instr.X != CoordUnspecified || instr.Y != CoordUnspecified))
							{
								int mx = instr.X, my = instr.Y;
								self.MouseMove(ref mx, ref my, ref Unsafe.AsRef<uint>(ref Unsafe.NullRef<uint>()), 0, instr.MoveOffset);
								self.DoMouseDelay();
								break;
							}

							for (var i = 0L; i < instr.RepeatCount; i++)
							{
								self.MouseEvent(((uint)instr.MouseEventType << 16) | instr.Vk, 0,
									instr.X, instr.Y);
								self.DoMouseDelay();
							}
							break;

						case SendInstructionType.MouseWheel:
							{
								if (instr.RequiresMouseMove(out int finalX, out int finalY))
									self.sim.SimulateMouseMovement((short)finalX, (short)finalY);
								self.sim.SimulateMouseWheel(instr.WheelAmount, instr.WheelDirection);
								self.DoMouseDelay();
							}
							break;
					}
				}

				ReleaseHeldTextMods();
			}
		}

		internal override void CleanupEventArray(long finalKeyDelay)
		{
			eventBuilder = null;
			eventCount = 0;
		}

		internal override void DoKeyDelay(long delay = long.MinValue)
		{
			if (delay == long.MinValue)
				delay = ThreadAccessors.A_KeyDelay;

			if (delay < 0)
				return;

			Flow.SleepWithoutInterruption(delay);
		}

		internal override void DoMouseDelay()
		{
			var mouseDelay = ThreadAccessors.A_MouseDelay;

			if (mouseDelay < 0)
				return;

			if (mouseDelay < 11)
				_ = Flow.Sleep(mouseDelay);
			else
				Flow.SleepWithoutInterruption(mouseDelay);
		}

		internal override nint GetFocusedKeybdLayout(nint window) => 0;
		internal override uint GetModifierLRState(bool explicitlyGet = false)
		{
			var ht = Script.TheScript.HookThread;

			// If the hook is running and cached state is acceptable, reuse the hook-tracked modifiers.
			if (ht is LinuxHookThread lht && ht.HasKbdHook() && !explicitlyGet)
				return lht.CurrentModifiersLR();

			var mods = 0u;

			// If no hook is present, try a logical query via X11 once.
			if (ht is LinuxHookThread lhtNoHook && !ht.HasKbdHook() && lhtNoHook.TryQueryModifierLRState(out var logicalMods))
			{
				mods = logicalMods;
			}
			else
			{
				if (ht.IsKeyDownAsync(VK_LSHIFT)) mods |= MOD_LSHIFT;
				if (ht.IsKeyDownAsync(VK_RSHIFT)) mods |= MOD_RSHIFT;
				if (ht.IsKeyDownAsync(VK_LCONTROL)) mods |= MOD_LCONTROL;
				if (ht.IsKeyDownAsync(VK_RCONTROL)) mods |= MOD_RCONTROL;
				if (ht.IsKeyDownAsync(VK_LMENU)) mods |= MOD_LALT;
				if (ht.IsKeyDownAsync(VK_RMENU)) mods |= MOD_RALT;
				if (ht.IsKeyDownAsync(VK_LWIN)) mods |= MOD_LWIN;
				if (ht.IsKeyDownAsync(VK_RWIN)) mods |= MOD_RWIN;
			}

			// Keep sender snapshots aligned when the hook is absent or explicit refresh requested.
			modifiersLRPhysical = mods;
			modifiersLRLogical = mods;
			modifiersLRLogicalNonIgnored = mods;

			return mods;
		}

		internal override void InitEventArray(int maxEvents, uint modifiersLR)
		{
			eventBuilder = sim.Sequence();
			eventCount = 0;
		}

		internal override void MouseClick(uint vk, int x, int y, long repeatCount, long speed, KeyEventTypes eventType, bool moveOffset)
		{
			for (var i = 0; i < repeatCount; i++)
			{
				MouseEvent(((uint)eventType << 16) | vk, 0, x, y);
				DoMouseDelay();
			}
		}

		internal override void MouseClickDrag(uint vk, int x1, int y1, int x2, int y2, long speed, bool relative)
		{
			var button = VkToMouseButton(vk);
			if (button == MouseButton.NoButton)
				return;

			if (sendMode == SendModes.Input && eventBuilder != null)
			{
				eventBuilder.AddMouseMovement((short)x1, (short)y1);
				eventBuilder.AddMousePress((short)x1, (short)y1, button);
				eventBuilder.AddMouseMovement((short)x2, (short)y2);
				eventBuilder.AddMouseRelease((short)x2, (short)y2, button);
				eventCount += 4;
			}
			else
			{
				sim.SimulateMouseMovement((short)x1, (short)y1);
				sim.SimulateMousePress((short)x1, (short)y1, button);
				sim.SimulateMouseMovement((short)x2, (short)y2);
				sim.SimulateMouseRelease((short)x2, (short)y2, button);
				DoMouseDelay();
			}
		}

		internal override void MouseEvent(uint eventFlags, uint data, int x = CoordUnspecified, int y = CoordUnspecified)
		{
			var button = VkToMouseButton(eventFlags & 0xFFFF);
			var type = (KeyEventTypes)(eventFlags >> 16);

			var finalX = x;
			var finalY = y;

			if (finalX == CoordUnspecified || finalY == CoordUnspecified)
			{
				var pos = System.Windows.Forms.Cursor.Position;
				finalX = pos.X;
				finalY = pos.Y;
			}

			if (sendMode == SendModes.Input && eventBuilder != null)
			{
				switch (type)
				{
					case KeyEventTypes.KeyDown:
						eventBuilder.AddMousePress((short)finalX, (short)finalY, button);
						break;
					case KeyEventTypes.KeyUp:
						eventBuilder.AddMouseRelease((short)finalX, (short)finalY, button);
						break;
					case KeyEventTypes.KeyDownAndUp:
						eventBuilder.AddMousePress((short)finalX, (short)finalY, button);
						eventBuilder.AddMouseRelease((short)finalX, (short)finalY, button);
						break;
				}
				eventCount++;
			}
			else
			{
				switch (type)
				{
					case KeyEventTypes.KeyDown:
						sim.SimulateMousePress((short)finalX, (short)finalY, button);
						break;
					case KeyEventTypes.KeyUp:
						sim.SimulateMouseRelease((short)finalX, (short)finalY, button);
						break;
					case KeyEventTypes.KeyDownAndUp:
						sim.SimulateMousePress((short)finalX, (short)finalY, button);
						sim.SimulateMouseRelease((short)finalX, (short)finalY, button);
						break;
				}

				DoMouseDelay();
			}
		}

		internal override void MouseMove(ref int x, ref int y, ref uint eventFlags, long speed, bool moveOffset)
		{
			if (sendMode == SendModes.Input && eventBuilder != null)
			{
				if (moveOffset)
					eventBuilder.AddMouseMovementRelative((short)x, (short)y);
				else
					eventBuilder.AddMouseMovement((short)x, (short)y);

				eventCount++;
			}
			else
			{
				if (moveOffset)
					sim.SimulateMouseMovementRelative((short)x, (short)y);
				else
					sim.SimulateMouseMovement((short)x, (short)y);

				DoMouseDelay();
			}
		}

		internal override int PbEventCount() => 0;

		internal override void SendEventArray(ref long finalKeyDelay, uint modsDuringSend)
		{
			if (eventBuilder == null)
				return;

			try
			{
				eventBuilder.Simulate();
			}
			finally
			{
				eventCount = 0;
				eventBuilder = null;
			}
		}

		internal override void SendKey(uint vk, uint sc, uint modifiersLR, uint modifiersLRPersistent
									   , long repeatCount, KeyEventTypes eventType, uint keyAsModifiersLR, nint targetWindow
									   , int x = CoordUnspecified, int y = CoordUnspecified, bool moveOffset = false)
		{
			var modifiersLRSpecified = (modifiersLR | modifiersLRPersistent);
			var script = Script.TheScript;
			var vkIsMouse = script.HookThread.IsMouseVK(vk);
			var tv = script.Threads.CurrentThread.configData;
			var sendLevel = tv.sendLevel;

			for (var i = 0L; i < repeatCount; ++i)
			{
				if (sendMode == SendModes.Event)
					LongOperationUpdateForSendKeys();

				if (keyAsModifiersLR == 0)
				{
					SetModifierLRState(modifiersLRSpecified, sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState()
									   , targetWindow, false, true, sendLevel != 0 ? KeyIgnoreLevel(sendLevel) : KeyIgnore);
				}

				if (vkIsMouse && targetWindow == 0)
				{
					MouseClick(vk, x, y, 1, tv.defaultMouseSpeed, eventType, moveOffset);
				}
				else
				{
					SendKeyEvent(eventType, vk, sc, targetWindow, true, KeyIgnoreLevel(sendLevel));
				}
			}

			if (keyAsModifiersLR == 0)
			{
				var stateNow = sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState();
				var winAltToBeReplaced = (stateNow & ~modifiersLRPersistent)
										 & (MOD_LWIN | MOD_RWIN | MOD_LALT | MOD_RALT);

				if (winAltToBeReplaced != 0)
				{
					SetModifierLRState(0u, winAltToBeReplaced, targetWindow, true, false);
				}
			}
		}

		internal override ResultType LayoutHasAltGrDirect(nint layout)
		{
			return ResultType.ConditionFalse;
		}

		internal override void SendKeyEventMenuMask(KeyEventTypes eventType, long extraInfo = KeyIgnoreAllExceptModifier)
		{
			// On Linux, mask Win/Alt release by sending a quick Ctrl tap to avoid menu activation in some environments.
			var maskVk = VK_LCONTROL;

			switch (eventType)
			{
				case KeyEventTypes.KeyDown:
					SendKeyEvent(KeyEventTypes.KeyDown, maskVk, 0u, default, false, extraInfo);
					break;
				case KeyEventTypes.KeyUp:
					SendKeyEvent(KeyEventTypes.KeyUp, maskVk, 0u, default, false, extraInfo);
					break;
				case KeyEventTypes.KeyDownAndUp:
					SendKeyEvent(KeyEventTypes.KeyDown, maskVk, 0u, default, false, extraInfo);
					SendKeyEvent(KeyEventTypes.KeyUp, maskVk, 0u, default, false, extraInfo);
					break;
			}
		}

		internal override int SiEventCount() => eventCount;

		internal override ToggleValueType ToggleKeyState(uint vk, ToggleValueType toggleValue)
		{
			bool capsOn = false, numOn = false, scrollOn = false;
			if (TheScript.HookThread is LinuxHookThread ht && ht.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn))
				return ToggleValueType.Invalid;

			bool current = vk switch
			{
				VK_CAPITAL => capsOn,
				VK_NUMLOCK => numOn,
				VK_SCROLL => scrollOn,
				_ => false
			};

			bool desiredOn = toggleValue == ToggleValueType.On || toggleValue == ToggleValueType.AlwaysOn;
			bool desiredOff = toggleValue == ToggleValueType.Off;

			if (desiredOn && !current || desiredOff && current || toggleValue == ToggleValueType.Toggle)
			{
				var code = SharpHookKeyMapper.VkToKeyCode(vk);
				if (code != KeyCode.VcUndefined)
					sim.SimulateKeyStroke(code);
			}

			if (desiredOn) return ToggleValueType.On;
			if (desiredOff) return ToggleValueType.Off;
			return ToggleValueType.Invalid;
		}

		protected internal override void LongOperationUpdate() { }
		protected internal override void LongOperationUpdateForSendKeys() { }

		protected internal override void SendKeyEvent(
			KeyEventTypes eventType,
			uint vk,
			uint sc = 0u,
			nint targetWindow = default,
			bool doKeyDelay = false,
			long extraInfo = 0)
		{
			var code = VkToKeyCode(vk);
			if (code == KeyCode.VcUndefined)
				return;

			if (sendMode == SendModes.Input && eventBuilder != null)
			{
				switch (eventType)
				{
					case KeyEventTypes.KeyDown:
						eventBuilder.AddKeyPress(code);
						break;
					case KeyEventTypes.KeyUp:
						eventBuilder.AddKeyRelease(code);
						break;
					case KeyEventTypes.KeyDownAndUp:
						eventBuilder.AddKeyPress(code);
						eventBuilder.AddKeyRelease(code);
						break;
				}
				eventCount++;
			}
			else
			{
				switch (eventType)
				{
					case KeyEventTypes.KeyDown:
						sim.SimulateKeyPress(code);
						break;
					case KeyEventTypes.KeyUp:
						sim.SimulateKeyRelease(code);
						break;
					case KeyEventTypes.KeyDownAndUp:
						sim.SimulateKeyStroke(code);
						break;
				}

				if (doKeyDelay)
					DoKeyDelay();
			}
		}

		internal override void SendKeys(string keys, SendRawModes sendRaw, SendModes sendModeOrig, nint targetWindow)
		{
			if (string.IsNullOrEmpty(keys))
				return;

			var ctx = new SendParseContext();
			SendParser.ParseBasic(keys, sendRaw, ctx);

			sendMode = sendModeOrig;
			if (sendMode == SendModes.InputThenPlay)
				sendMode = SendModes.Input;
			else if (sendMode == SendModes.Play)
				sendMode = SendModes.Event;

			SendExecutor.Execute(ctx, sendMode, this, backend);
		}

		protected override void RegisterHook() { }

		internal sealed class SharpHookKeySimulationBackend
		{
			internal readonly IEventSimulator sim;

			public SharpHookKeySimulationBackend(IEventSimulator? sim = null)
				=> this.sim = sim ?? new EventSimulator();

				public void KeyDown(uint vk)
				{
					var code = SharpHookKeyMapper.VkToKeyCode(vk);
					if (code != KeyCode.VcUndefined)
					{
						Console.WriteLine($"[SendEmit] KeyDown vk={vk} code={code}");
						sim.SimulateKeyPress(code);
					}
				}

				public void KeyUp(uint vk)
				{
					var code = SharpHookKeyMapper.VkToKeyCode(vk);
					if (code != KeyCode.VcUndefined)
					{
						Console.WriteLine($"[SendEmit] KeyUp vk={vk} code={code}");
						sim.SimulateKeyRelease(code);
					}
				}

				public void KeyStroke(uint vk)
				{
					var code = SharpHookKeyMapper.VkToKeyCode(vk);
					if (code != KeyCode.VcUndefined)
					{
						Console.WriteLine($"[SendEmit] KeyStroke vk={vk} code={code}");
						sim.SimulateKeyStroke(code);
					}
				}

			public IKeySimulationSequence BeginSequence()
				=> new SharpHookKeySequence(sim);
		}

		internal sealed class SharpHookKeySequence : IKeySimulationSequence
		{
			private readonly IEventSimulationSequenceBuilder builder;
			private bool committed;

			public SharpHookKeySequence(IEventSimulator sim)
			{
				builder = sim.Sequence();
			}

			public void AddKeyDown(uint vk)
			{
				var code = SharpHookKeyMapper.VkToKeyCode(vk);
				if (code != KeyCode.VcUndefined)
				{
					Console.WriteLine($"[SendEmit] Seq KeyDown vk={vk} code={code}");
					builder.AddKeyPress(code);
				}
			}

			public void AddKeyUp(uint vk)
			{
				var code = SharpHookKeyMapper.VkToKeyCode(vk);
				if (code != KeyCode.VcUndefined)
				{
					Console.WriteLine($"[SendEmit] Seq KeyUp vk={vk} code={code}");
					builder.AddKeyRelease(code);
				}
			}

			public void AddKeyStroke(uint vk)
			{
				var code = SharpHookKeyMapper.VkToKeyCode(vk);
				if (code != KeyCode.VcUndefined)
				{
					Console.WriteLine($"[SendEmit] Seq KeyStroke vk={vk} code={code}");
					builder.AddKeyPress(code);
					builder.AddKeyRelease(code);
				}
			}

			public void Commit()
			{
				if (committed) return;
				committed = true;
				Console.WriteLine("[SendEmit] Seq Commit");
				builder.Simulate();
			}

			public void Dispose()
			{
				if (!committed)
					Commit();
			}
		}
	}
}

#endif
