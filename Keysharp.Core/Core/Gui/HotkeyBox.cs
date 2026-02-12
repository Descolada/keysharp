namespace Keysharp.Core
{
#if !WINDOWS
		using Keys = Eto.Forms.Keys;
#endif
	internal class HotkeyBox : TextBox
	{
		private Keys key, mod;
		private bool updatingDisplayText;

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public Limits Limit { get; set; }

		public HotkeyBox()
		{
			key = mod = Keys.None;
			Limit = Limits.None;
			Text = "";
#if WINDOWS
			Multiline = false;
			ShortcutsEnabled = false;
			ContextMenuStrip = new ContextMenuStrip();
			PreviewKeyDown += (sender, e) =>
			{
				if (e.KeyCode == Keys.Tab || e.Control || e.Alt)
					e.IsInputKey = true;
			};
			KeyPress += (sender, e) => e.Handled = true;
#endif
			KeyUp += (sender, e) =>
			{
				var isModKey = IsModifierKey(e.KeyCode);

				if (isModKey)
				{
					if (key == Keys.None)
					{
						mod = e.Modifiers;

						if (mod == Keys.None)
							key = Keys.None;

						UpdateDisplayText();
					}
				}
				else if (e.KeyCode == Keys.None && e.Modifiers == Keys.None)
				{
					key = Keys.None;
					UpdateDisplayText();
				}
			};
			KeyDown += (sender, e) =>
			{
				e.Handled = true;
#if WINDOWS
				e.SuppressKeyPress = true;
#endif
				//if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
				//{
				//  key = mod = Keys.None;
				//}
				//else
				{
					key = e.KeyCode;
					mod = e.Modifiers;
					Validate();
				}
				UpdateDisplayText();
			};
		}

		private static bool IsModifierKey(Keys keyCode)
		{
#if WINDOWS
			return
				keyCode == Keys.ShiftKey || keyCode == Keys.LShiftKey || keyCode == Keys.RShiftKey ||
				keyCode == Keys.ControlKey || keyCode == Keys.LControlKey || keyCode == Keys.RControlKey ||
				keyCode == Keys.Menu || keyCode == Keys.LMenu || keyCode == Keys.RMenu;
#else
			return
				keyCode == Keys.Shift || keyCode == Keys.LeftShift || keyCode == Keys.RightShift ||
				keyCode == Keys.Control || keyCode == Keys.LeftControl || keyCode == Keys.RightControl ||
				keyCode == Keys.Alt || keyCode == Keys.LeftAlt || keyCode == Keys.RightAlt;
#endif
		}

		public override string Text
		{
			get
			{
				if (updatingDisplayText)
					return base.Text;

				var str = "";

				if ((mod & Keys.Control) == Keys.Control)
					str += Keyword_ModifierCtrl;

				if ((mod & Keys.Shift) == Keys.Shift)
					str += Keyword_ModifierShift;

				if ((mod & Keys.Alt) == Keys.Alt)
					str += Keyword_ModifierAlt;

				if (key == Keys.None)
					return mod == Keys.None ? "None" : str;

				return str + key.ToString();
			}
			set
			{
				if (value != null) 
				{
					Keys keys = Keys.None, mods = Keys.None;

					if (!value.Equals("None", StringComparison.OrdinalIgnoreCase))
					{
						foreach (var ch in value)
						{
							switch (ch)
							{
								case Keyword_ModifierAlt: mods |= Keys.Alt; break;

								case Keyword_ModifierCtrl: mods |= Keys.Control; break;

								case Keyword_ModifierShift: mods |= Keys.Shift; break;

								default:
								{
									if (Enum.TryParse(ch.ToString(), true, out Keys k))
										keys = k;

									break;
								}
							}
						}
					}

					key = keys;
					mod = mods;
					Validate();
				}

				UpdateDisplayText();
			}
		}

		private void UpdateDisplayText()
		{
			if (updatingDisplayText)
				return;

			updatingDisplayText = true;
			try
			{
				var buf = new StringBuilder(45);
				const string sep = " + ";

				if ((mod & Keys.Control) == Keys.Control)
				{
					_ = buf.Append(Enum.GetName(typeof(Keys), Keys.Control));
					_ = buf.Append(sep);
				}

				if ((mod & Keys.Shift) == Keys.Shift)
				{
					_ = buf.Append(Enum.GetName(typeof(Keys), Keys.Shift));
					_ = buf.Append(sep);
				}

				if ((mod & Keys.Alt) == Keys.Alt)
				{
					_ = buf.Append(Enum.GetName(typeof(Keys), Keys.Alt));
					_ = buf.Append(sep);
				}

				if (key != Keys.None)
				{
					_ = buf.Append(key.ToString());
				}

				var newText = key != Keys.None || mod != Keys.None ? buf.ToString() : "None";

				if (!string.Equals(base.Text, newText, StringComparison.Ordinal))
					base.Text = newText;
			}
			finally
			{
				updatingDisplayText = false;
			}
		}

		private void Validate()
		{
#if WINDOWS
			Keys[,] sym = { { Keys.Control, Keys.ControlKey }, { Keys.Shift, Keys.ShiftKey }, { Keys.Alt, Keys.Menu } };

			for (var i = 0; i < 3; i++)
			{
				if (key == sym[i, 1] && (mod & sym[i, 0]) == sym[i, 0])
					key = Keys.None;
			}
#else
			if ((mod & Keys.Control) == Keys.Control &&
				(key == Keys.Control || key == Keys.LeftControl || key == Keys.RightControl))
				key = Keys.None;

			if ((mod & Keys.Shift) == Keys.Shift &&
				(key == Keys.Shift || key == Keys.LeftShift || key == Keys.RightShift))
				key = Keys.None;

			if ((mod & Keys.Alt) == Keys.Alt &&
				(key == Keys.Alt || key == Keys.LeftAlt || key == Keys.RightAlt))
				key = Keys.None;
#endif

			if ((Limit & Limits.PreventUnmodified) == Limits.PreventUnmodified)
			{
				if (mod == Keys.None)
					key = Keys.None;
			}

			if ((Limit & Limits.PreventShiftOnly) == Limits.PreventShiftOnly)
			{
				if (mod == Keys.Shift)
					key = mod = Keys.None;
			}

			if ((Limit & Limits.PreventControlOnly) == Limits.PreventControlOnly)
			{
				if (mod == Keys.Control)
					key = mod = Keys.None;
			}

			if ((Limit & Limits.PreventAltOnly) == Limits.PreventAltOnly)
			{
				if (mod == Keys.Alt)
					key = mod = Keys.None;
			}

			if ((Limit & Limits.PreventShiftControl) == Limits.PreventShiftControl)
			{
				if ((mod & Keys.Shift) == Keys.Shift && (mod & Keys.Control) == Keys.Control && (mod & Keys.Alt) != Keys.Alt)
					key = mod = Keys.None;
			}

			if ((Limit & Limits.PreventShiftAlt) == Limits.PreventShiftAlt)
			{
				if ((mod & Keys.Shift) == Keys.Shift && (mod & Keys.Control) != Keys.Control && (mod & Keys.Control) == Keys.Alt)
					key = mod = Keys.None;
			}

			if ((Limit & Limits.PreventShiftControlAlt) == Limits.PreventShiftControlAlt)
			{
				if ((mod & Keys.Shift) == Keys.Shift && (mod & Keys.Control) == Keys.Control && (mod & Keys.Control) == Keys.Alt)
					key = mod = Keys.None;
			}
		}

		[Flags]
		public enum Limits
		{
			None = 0,
			PreventUnmodified = 1,
			PreventShiftOnly = 2,
			PreventControlOnly = 4,
			PreventAltOnly = 8,
			PreventShiftControl = 16,
			PreventShiftAlt = 32,
			PreventShiftControlAlt = 128,
		}
	}
}
