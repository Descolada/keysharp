using Timer = System.Windows.Forms.Timer;

namespace Keysharp.Builtins
{
	internal class InputDialog : KeysharpForm
	{
		private const int Unspecified = int.MinValue;
		private readonly KeysharpButton btnCancel;
		private readonly KeysharpButton btnOK;
		private readonly FlowLayoutPanel buttonLayout;
		private readonly TableLayoutPanel contentLayout;
		private readonly KeysharpLabel prompt;
		private readonly int requestedClientHeight;
		private readonly int requestedClientWidth;
		private readonly int requestedLeft;
		private readonly int requestedTop;
		private Timer timer;
		private readonly TextBox txtMessage;
		private bool layoutPrepared;

		private int ButtonGap => ScalePixels(8);
		private int ButtonTopMargin => ScalePixels(8);
		private int DialogPadding => ScalePixels(12);
		private int DialogBottomPadding => ScalePixels(6);
		private int WorkAreaMargin => ScalePixels(32);

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Default { get; set; }
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Message { get => txtMessage.Text; set => txtMessage.Text = value; }
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public string PasswordChar
		{
			get => txtMessage.PasswordChar.ToString();
			set
			{
				if (string.IsNullOrEmpty(value))
					txtMessage.UseSystemPasswordChar = true;
				else
					txtMessage.PasswordChar = value[0];
			}
		}
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Prompt { get => prompt.Text; set => prompt.Text = value; }
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Result { get; private set; } = "";
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Timeout { get; set; }
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Title { get => Text; set => Text = value; }

		protected override CreateParams CreateParams
		{
			get
			{
				var cp = base.CreateParams;
				// Use the Win32 dialog class so GetClassName() returns "#32770",
				// matching AHK-style scripts that look for native dialog windows.
				cp.ClassName = "#32770";
				return cp;
			}
		}

		public InputDialog(int clientWidth = Unspecified, int clientHeight = Unspecified, int left = Unspecified, int top = Unspecified)
		{
			requestedClientWidth = clientWidth;
			requestedClientHeight = clientHeight;
			requestedLeft = left;
			requestedTop = top;
			prompt = new KeysharpLabel { Name = "Prompt", AutoSize = true, BackColor = Color.Transparent, Dock = DockStyle.Top, Margin = Padding.Empty };
			btnOK = new KeysharpButton { DialogResult = DialogResult.OK, Name = "OK", TabIndex = 1, Text = "OK", AutoSize = true, Margin = new Padding(0, 0, ButtonGap, 0) };
			btnCancel = new KeysharpButton { DialogResult = DialogResult.Cancel, Name = "Cancel", TabIndex = 2, Text = "Cancel", AutoSize = true, Margin = Padding.Empty };
			buttonLayout = new FlowLayoutPanel
			{
				Anchor = AnchorStyles.None,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				FlowDirection = FlowDirection.LeftToRight,
				Margin = new Padding(0, ButtonTopMargin, 0, 0),
				Padding = Padding.Empty,
				WrapContents = false
			};
			contentLayout = new TableLayoutPanel
			{
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				ColumnCount = 1,
				Dock = DockStyle.Top,
				GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
				Margin = Padding.Empty,
				Padding = new Padding(DialogPadding, DialogPadding, DialogPadding, DialogBottomPadding),
				RowCount = 3
			};
			txtMessage = new TextBox { Name = "Message", TabIndex = 0, Dock = DockStyle.Top, Margin = new Padding(0, DialogPadding, 0, 0) };
			FormClosing += (_, _) =>
			{
				if (Result.Length == 0)
					Result = "Cancel";
			};
			btnOK.Click += (_, _) => CloseWith("OK");
			btnCancel.Click += (_, _) => CloseWith("Cancel");
			Load += InputDialog_Load;
			Resize += (_, _) =>
			{
				if (!layoutPrepared)
					return;

				ApplyLayoutWidth(ClientSize.Width);
				contentLayout.PerformLayout();
				UpdateMinimumSize(GetTargetWorkingArea());
			};
			Shown += InputDialog_Shown;
			txtMessage.KeyDown += TxtMessage_KeyDown;
		}

		private void CloseWith(string result)
		{
			Result = result;
			Hide();
		}

		private void InitializeComponent()
		{
			SuspendLayout();
			contentLayout.RowStyles.Add(new RowStyle());
			contentLayout.RowStyles.Add(new RowStyle());
			contentLayout.RowStyles.Add(new RowStyle());
			contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			contentLayout.Controls.Add(prompt, 0, 0);
			contentLayout.Controls.Add(txtMessage, 0, 1);
			contentLayout.Controls.Add(buttonLayout, 0, 2);
			buttonLayout.Controls.Add(btnOK);
			buttonLayout.Controls.Add(btnCancel);
			Controls.Add(contentLayout);
			ShowIcon = true;
			MaximizeBox = false;
			MinimizeBox = false;
			AutoScaleMode = AutoScaleMode.Dpi;
			FormBorderStyle = FormBorderStyle.Sizable;
			SizeGripStyle = SizeGripStyle.Show;
			StartPosition = FormStartPosition.Manual;
			AcceptButton = btnOK;
			CancelButton = btnCancel;
			ActiveControl = txtMessage;
			Name = "KeysharpInputBox";
			ResumeLayout(false);
			PerformLayout();
		}

		internal void PrepareForShow()
		{
			if (layoutPrepared)
				return;

			layoutPrepared = true;
			InitializeComponent();
			txtMessage.Text = Default;
			var workingArea = GetTargetWorkingArea();
			var minimumWidth = GetMinimumClientWidth();
			var width = requestedClientWidth != Unspecified
				? requestedClientWidth
				: GetDefaultClientWidth(minimumWidth, workingArea);
			var preferredSize = GetPreferredClientSize(width, workingArea);
			var height = requestedClientHeight != Unspecified ? requestedClientHeight : preferredSize.Height;
			ClientSize = new Size(width, height);
			UpdateMinimumSize(workingArea);

			Left = requestedLeft != Unspecified ? requestedLeft : workingArea.Left + ((workingArea.Width - Width) / 2);
			Top = requestedTop != Unspecified ? requestedTop : workingArea.Top + ((workingArea.Height - Height) / 2);
		}

		private Size GetPreferredClientSize(int clientWidth, Rectangle workingArea)
		{
			ApplyLayoutWidth(clientWidth);
			contentLayout.PerformLayout();
			var preferredSize = contentLayout.GetPreferredSize(new Size(clientWidth, 0));
			var maxHeight = Math.Max(1, workingArea.Height - WorkAreaMargin);
			return new Size(clientWidth, Math.Min(preferredSize.Height, maxHeight));
		}

		private void UpdateMinimumSize(Rectangle workingArea)
		{
			var minimumClientWidth = requestedClientWidth != Unspecified ? Math.Min(requestedClientWidth, GetMinimumClientWidth()) : GetMinimumClientWidth();
			var minimumClientHeight = GetPreferredClientSize(ClientSize.Width, workingArea).Height;
			if (requestedClientHeight != Unspecified)
				minimumClientHeight = Math.Min(requestedClientHeight, minimumClientHeight);
			MinimumSize = SizeFromClientSize(new Size(minimumClientWidth, minimumClientHeight));
		}

		private int GetDefaultClientWidth(int minimumWidth, Rectangle workingArea)
		{
			var noWrapPromptWidth = prompt.GetPreferredSize(Size.Empty).Width;
			var naturalWidth = contentLayout.Padding.Horizontal + Math.Max(buttonLayout.GetPreferredSize(Size.Empty).Width, noWrapPromptWidth);
			var maxWidth = Math.Max(minimumWidth, workingArea.Width - WorkAreaMargin);
			return Math.Clamp(naturalWidth, minimumWidth, maxWidth);
		}

		private int GetMinimumClientWidth() => contentLayout.Padding.Horizontal + buttonLayout.GetPreferredSize(Size.Empty).Width;

		private void ApplyLayoutWidth(int clientWidth)
		{
			var contentWidth = Math.Max(1, clientWidth - contentLayout.Padding.Horizontal);
			contentLayout.MaximumSize = new Size(clientWidth, 0);
			contentLayout.Width = clientWidth;
			prompt.MaximumSize = new Size(contentWidth, 0);
			txtMessage.Width = contentWidth;
		}

		private Rectangle GetTargetWorkingArea()
		{
			var primaryArea = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, Width, Height);
			var referencePoint = new Point(
				requestedLeft != Unspecified ? requestedLeft : primaryArea.Left + (primaryArea.Width / 2),
				requestedTop != Unspecified ? requestedTop : primaryArea.Top + (primaryArea.Height / 2));
			return System.Windows.Forms.Screen.FromPoint(referencePoint).WorkingArea;
		}

		private int ScalePixels(int logicalPixels) => (int)Math.Round(logicalPixels * (DeviceDpi / 96.0));

		private void InputDialog_Load(object sender, EventArgs e)
		{
			PrepareForShow();
			EnsureHandlesCreated(this);
			_ = WindowsAPI.SendMessage(Handle, (uint)WindowsAPI.WM_INITDIALOG, txtMessage.Handle, 0);
			ApplyDialogCtlColorTheme();
		}

		private void ApplyDialogCtlColorTheme()
		{
			ApplyCtlColorTheme(this, (uint)WindowsAPI.WM_CTLCOLORDLG, false);
			ApplyCtlColorTheme(prompt, (uint)WindowsAPI.WM_CTLCOLORSTATIC, false);
			ApplyCtlColorTheme(txtMessage, (uint)WindowsAPI.WM_CTLCOLOREDIT, false);
			ApplyCtlColorTheme(btnOK, (uint)WindowsAPI.WM_CTLCOLORBTN, false);
			ApplyCtlColorTheme(btnCancel, (uint)WindowsAPI.WM_CTLCOLORBTN, false);
			QueueCtlColorThemeRefresh();
		}

		private static void EnsureHandlesCreated(Control parent)
		{
			foreach (Control child in parent.Controls)
			{
				_ = child.Handle;
				if (child.Controls.Count > 0)
					EnsureHandlesCreated(child);
			}
		}

		private void InputDialog_Shown(object sender, EventArgs e)
		{
			var script = Script.TheScript;
			if (script.Tray?.Icon != null)
				Icon = script.Tray.Icon;
			Activate();
			BringToFront();
			txtMessage.Focus();
			txtMessage.SelectAll();
			if (Timeout > 0)
			{
				timer = new Timer { Interval = Timeout * 1000 };
				timer.Tick += (_, _) =>
				{
					timer.Enabled = false;
					Result = "Timeout";
					Hide();
				};
				timer.Enabled = true;
			}
		}

		private void TxtMessage_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Enter)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				btnOK.PerformClick();
			}
			else if (e.KeyCode == Keys.Escape)
				btnCancel.PerformClick();
		}
	}
}
