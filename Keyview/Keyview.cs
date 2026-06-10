using Antlr4.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#if WINDOWS
using ScintillaNET;
#endif

namespace Keyview
{
	/// <summary>
	/// Much of the Scintilla-related code was taken from: https://github.com/robinrodricks/ScintillaNET.Demo
	/// </summary>
#if WINDOWS
	internal partial class Keyview : Form
	{
		/// <summary>
		/// the background color of the text area
		/// </summary>
		//private const int BACK_COLOR = 0x2A211C;
		private const int BACK_COLOR = 0xEEEEEE;

		/// <summary>
		/// change this to whatever margin you want the bookmarks/breakpoints to show in
		/// </summary>
		private const int BOOKMARK_MARGIN = 2;

		private const int BOOKMARK_MARKER = 2;

		/// <summary>
		/// set this true to show circular buttons for code folding (the [+] and [-] buttons on the margin)
		/// </summary>
		private const bool CODEFOLDING_CIRCULAR = true;

		/// <summary>
		/// change this to whatever margin you want the code folding tree (+/-) to show in
		/// </summary>
		private const int FOLDING_MARGIN = 3;

		/// <summary>
		/// default text color of the text area
		/// </summary>
		private const int FORE_COLOR = 0x858585;

		/// <summary>
		/// change this to whatever margin you want the line numbers to show in
		/// </summary>
		private const int NUMBER_MARGIN = 1;

		private static readonly string keywords1 = "true false this thishotkey super unset isset " + Keywords.GetKeywords();
		private readonly string keywords2;
		private readonly Button btnCopyFullCode = new ();
		private readonly Button btnCompileScript = new ();
		private readonly CheckBox chkFullCode = new ();
		private readonly ToolStripLabel documentStatusLabel = new ();
		private readonly string lastrun;
		private readonly UITimer timer = new ();
		private readonly char[] trimend = ['\n', '\r'];
		private readonly double updateFreqSeconds = 1;
		private readonly CompilerHelper ch = new ();
		private readonly CSharpStyler csStyler = new ();
		private bool force = false;
		private bool isCompiling = false;
		private string fullCode = "";
		private DateTime lastCompileTime = DateTime.UtcNow;
		private DateTime lastKeyTime = DateTime.UtcNow;
		private bool SearchIsOpen = false;
		private string trimmedCode = "";
		private readonly string trimstr = "{}\t";
		private Process scriptProcess = null;
		private readonly Button btnRunScript = new ();
		private readonly KeyviewDocumentState document = new ();
		private string baseTitle;
		private bool suppressDocumentChange;
		private readonly Dictionary<string, string> btnRunScriptText = new Dictionary<string, string>()
		{
			{ "Run", "▶ Run script (F9)" },
			{ "Stop", "⏹ Stop script (F9)" }
		};

		public Keyview(string initialFile = null)
		{
			InitializeComponent();
			keywords2 = Script.TheScript.GetPublicStaticPropertyNames();
			lastrun = $"{Accessors.A_AppData}/Keysharp/lastkeyviewrun.txt";
			Icon = Script.TheScript.normalIcon;
			btnCopyFullCode.Text = "Copy full code";
			btnCopyFullCode.Click += CopyFullCode_Click;
			btnCopyFullCode.Margin = new Padding(15);
			var host = new ToolStripControlHost(btnCopyFullCode)
			{
				Alignment = ToolStripItemAlignment.Right
			};
			_ = toolStrip1.Items.Add(host);
			chkFullCode.Text = "Full code";
			chkFullCode.CheckStateChanged += chkFullCode_CheckStateChanged;
			host = new ToolStripControlHost(chkFullCode)
			{
				Alignment = ToolStripItemAlignment.Right
			};
			_ = toolStrip1.Items.Add(host);
			Text += $" {Assembly.GetExecutingAssembly().GetName().Version}";
			baseTitle = Text;
			btnRunScript.Text = btnRunScriptText["Run"];
			btnRunScript.Margin = new Padding(15);
			host = new ToolStripControlHost(btnRunScript)
			{
				Alignment = ToolStripItemAlignment.Right
			};
			_ = toolStrip1.Items.Add(host);
			btnRunScript.Enabled = false;
			btnRunScript.Click += RunScript_Click;
			btnCompileScript.Text = "Compile .cks";
			btnCompileScript.Margin = new Padding(15);
			btnCompileScript.Click += (_, _) => CompileDocument();
			host = new ToolStripControlHost(btnCompileScript);
			_ = toolStrip1.Items.Add(host);
			documentStatusLabel.Alignment = ToolStripItemAlignment.Left;
			documentStatusLabel.AutoSize = false;
			documentStatusLabel.Width = 600;
			documentStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
			toolStrip1.Items.Insert(0, documentStatusLabel);

			if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
			{
				LoadDataFromFile(initialFile);
			}
			else if (File.Exists(lastrun))
			{
				LoadScratchDocument();
			}
			else
				UpdateDocumentUi();
		}

		private static Color IntToColor(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

		//private void TxtIn_StyleNeeded(object sender, StyleNeededEventArgs e)
		//{
		//  var scintilla = sender as Scintilla;
		//  var startPos = scintilla.GetEndStyled();
		//  var endPos = e.Position;
		//  // Start styling
		//  //scintilla.StartStyling(startPos);
		//  //while (startPos < endPos)
		//  //{
		//  //  var c = (char)scintilla.GetCharAt(startPos);
		//  //  if (c == ';')
		//  //  {
		//  //      scintilla.SetStyling(endPos - startPos, Style.Cpp.CommentLine);
		//  //      break;
		//  //  }
		//  //  startPos++;
		//  //}
		//}
		private void BtnClearSearch_Click(object sender, EventArgs e) => CloseSearch();

		private void BtnNextSearch_Click(object sender, EventArgs e) => SearchManager.Find(true, false);

		private void BtnPrevSearch_Click(object sender, EventArgs e) => SearchManager.Find(false, false);

		private void chkFullCode_CheckStateChanged(object sender, EventArgs e) => SetTxtOut(chkFullCode.Checked ? fullCode : trimmedCode);

		private void clearSelectionToolStripMenuItem_Click(object sender, EventArgs e)
		{
			// Reset selection to the start without changing the caret position more than necessary.
			txtIn.SetEmptySelection(0);
			lastKeyTime = DateTime.UtcNow;
		}

		private void CloseSearch()
		{
			if (SearchIsOpen)
			{
				SearchIsOpen = false;
				InvokeIfNeeded(delegate ()
				{
					PanelSearch.Visible = false;
				});
			}
		}

		private void collapseAllToolStripMenuItem_Click(object sender, EventArgs e)
		{
			txtIn.FoldAll(FoldAction.Contract);
		}

		private void CopyFullCode_Click(object sender, EventArgs e)
		{
			try
			{
				if (fullCode != "")
					Clipboard.SetText(fullCode);
				else
					Clipboard.SetText(txtOut.Text);
			}
			catch (Exception ex)
			{
				_ = MessageBox.Show($"Copying code failed: {ex.Message}", "Keyview", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void copyToolStripMenuItem_Click(object sender, EventArgs e)
		{
			txtIn.Copy();
			lastKeyTime = DateTime.UtcNow;
		}

		private void cutToolStripMenuItem_Click(object sender, EventArgs e)
		{
			txtIn.Cut();
			lastKeyTime = DateTime.UtcNow;
		}

		private void expandAllToolStripMenuItem_Click(object sender, EventArgs e)
		{
			txtIn.FoldAll(FoldAction.Expand);
		}

		private void findAndReplaceToolStripMenuItem_Click(object sender, EventArgs e) => OpenReplaceDialog();

		private void findDialogToolStripMenuItem_Click(object sender, EventArgs e) => OpenFindDialog();

		private void findToolStripMenuItem_Click(object sender, EventArgs e) => OpenSearch();

		private void GenerateKeystrokes(string keys)
		{
			HotKeyManager.Enable = false;
			_ = txtIn.Focus();
			SendKeys.Send(keys);
			HotKeyManager.Enable = true;
		}

		private void hiddenCharactersToolStripMenuItem_Click(object sender, EventArgs e)
		{
			hiddenCharactersItem.Checked = !hiddenCharactersItem.Checked;
			txtIn.ViewWhitespace = hiddenCharactersItem.Checked ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
		}

		private void Indent()
		{
			//We use this hack to send "Shift+Tab" to scintilla, since there is no known API to indent,
			//although the indentation function exists. Pressing TAB with the editor focused confirms this.
			GenerateKeystrokes("{TAB}");
			lastKeyTime = DateTime.UtcNow;
		}

		private void indentGuidesToolStripMenuItem_Click(object sender, EventArgs e)
		{
			indentGuidesItem.Checked = !indentGuidesItem.Checked;
			txtIn.IndentationGuides = indentGuidesItem.Checked ? IndentView.LookBoth : IndentView.None;
		}

		private void indentSelectionToolStripMenuItem_Click(object sender, EventArgs e) => Indent();

		//private void InitBookmarkMargin()
		//{
		//  //TextArea.SetFoldMarginColor(true, IntToColor(BACK_COLOR));
		//  var margin = txtIn.Margins[BOOKMARK_MARGIN];
		//  margin.Width = 20;
		//  margin.Sensitive = true;
		//  margin.Type = MarginType.Symbol;
		//  margin.Mask = 1 << BOOKMARK_MARKER;
		//  //margin.Cursor = MarginCursor.Arrow;
		//  var marker = txtIn.Markers[BOOKMARK_MARKER];
		//  marker.Symbol = MarkerSymbol.Circle;
		//  marker.SetBackColor(IntToColor(0xFF003B));
		//  marker.SetForeColor(IntToColor(0x000000));
		//  marker.SetAlpha(100);
		//}
		private void InitCodeFolding(Scintilla txt)
		{
			txt.SetFoldMarginColor(true, IntToColor(BACK_COLOR));
			txt.SetFoldMarginHighlightColor(true, IntToColor(BACK_COLOR));
			//Enable code folding.
			txt.SetProperty("fold", "1");
			txt.SetProperty("fold.compact", "1");
			// Configure a margin to display folding symbols
			txt.Margins[FOLDING_MARGIN].Type = MarginType.Symbol;
			txt.Margins[FOLDING_MARGIN].Mask = Marker.MaskFolders;
			txt.Margins[FOLDING_MARGIN].Sensitive = true;
			txt.Margins[FOLDING_MARGIN].Width = 20;

			//Set colors for all folding markers.
			for (int i = 25; i <= 31; i++)
			{
				txt.Markers[i].SetForeColor(IntToColor(BACK_COLOR)); // styles for [+] and [-]
				txt.Markers[i].SetBackColor(IntToColor(FORE_COLOR)); // styles for [+] and [-]
			}

			//Configure folding markers with respective symbols.
			txt.Markers[Marker.Folder].Symbol = CODEFOLDING_CIRCULAR ? MarkerSymbol.CirclePlus : MarkerSymbol.BoxPlus;
			txt.Markers[Marker.FolderOpen].Symbol = CODEFOLDING_CIRCULAR ? MarkerSymbol.CircleMinus : MarkerSymbol.BoxMinus;
			txt.Markers[Marker.FolderEnd].Symbol = CODEFOLDING_CIRCULAR ? MarkerSymbol.CirclePlusConnected : MarkerSymbol.BoxPlusConnected;
			txt.Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
			txt.Markers[Marker.FolderOpenMid].Symbol = CODEFOLDING_CIRCULAR ? MarkerSymbol.CircleMinusConnected : MarkerSymbol.BoxMinusConnected;
			txt.Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
			txt.Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;
			//Enable automatic folding.
			txt.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;
		}

		private void InitColors(Scintilla txt) => txt.CaretForeColor = Color.Black;

		private void InitNumberMargin(Scintilla txt)
		{
			txt.Styles[Style.LineNumber].BackColor = IntToColor(BACK_COLOR);
			txt.Styles[Style.LineNumber].ForeColor = IntToColor(FORE_COLOR);
			txt.Styles[Style.IndentGuide].ForeColor = IntToColor(FORE_COLOR);
			txt.Styles[Style.IndentGuide].BackColor = IntToColor(BACK_COLOR);
			var nums = txt.Margins[NUMBER_MARGIN];
			nums.Width = 30;
			nums.Type = MarginType.Number;
			nums.Sensitive = true;
			nums.Mask = 0;
			txt.MarginClick += txtIn_MarginClick;

			UpdateNumberMarginWidth(txt);
		}

		private void UpdateNumberMarginWidth(Scintilla txt)
		{
			// how many lines do we have?
			int maxLine = Math.Max(1, txt.Lines.Count);      // avoid 0
			int digits = (int)Math.Log10(maxLine) + 1;       // 1 for 1..9, 2 for 10..99, etc.

			// width in pixels needed to render that many '9' with the line-number style
			int px = txt.TextWidth(Style.LineNumber, new string('9', digits));

			// a little breathing room for padding glyphs
			txt.Margins[NUMBER_MARGIN].Width = px + 8;
		}

		private void InitSyntaxColoring(Scintilla txt)
		{
			//Configure the default style.
			txt.StyleResetDefault();
			txt.Styles[Style.Default].Font = "Consolas";
			txt.Styles[Style.Default].Size = 10;
			//txt.Styles[Style.Default].BackColor = IntToColor(0xFFFCE1);
			//txt.Styles[Style.Default].BackColor = IntToColor(0x212121);
			//txt.Styles[Style.Default].ForeColor = IntToColor(0xFFFFFF);
			txt.StyleClearAll();
			var orig = false;

			if (orig)
			{
				txt.Styles[Style.Cpp.Identifier].ForeColor = IntToColor(0xD0DAE2);
				txt.Styles[Style.Cpp.Comment].ForeColor = IntToColor(0xBD758B);
				txt.Styles[Style.Cpp.CommentLine].ForeColor = IntToColor(0x40BF57);
				txt.Styles[Style.Cpp.CommentDoc].ForeColor = IntToColor(0x2FAE35);
				txt.Styles[Style.Cpp.Number].ForeColor = IntToColor(0xFFFF00);
				txt.Styles[Style.Cpp.String].ForeColor = IntToColor(0xFFFF00);
				txt.Styles[Style.Cpp.Character].ForeColor = IntToColor(0xE95454);
				txt.Styles[Style.Cpp.Preprocessor].ForeColor = IntToColor(0x8AAFEE);
				txt.Styles[Style.Cpp.Operator].ForeColor = IntToColor(0xE0E0E0);
				txt.Styles[Style.Cpp.CommentLineDoc].ForeColor = IntToColor(0x77A7DB);
				txt.Styles[Style.Cpp.Word].ForeColor = IntToColor(0x48A8EE);
				txt.Styles[Style.Cpp.Word2].ForeColor = IntToColor(0xF98906);
				txt.SelectionBackColor = IntToColor(0x114D9C);
			}
			else
			{
				txt.Styles[Style.Cpp.Default].ForeColor = Color.Black;
				txt.Styles[Style.Cpp.Comment].ForeColor = Color.FromArgb(0, 128, 0); // Green
				txt.Styles[Style.Cpp.CommentLine].ForeColor = Color.FromArgb(0, 128, 0); // Green
				txt.Styles[Style.Cpp.CommentLineDoc].ForeColor = Color.FromArgb(0, 128, 0); // Green
				txt.Styles[Style.Cpp.Number].ForeColor = Color.DarkOliveGreen;
				txt.Styles[Style.Cpp.String].ForeColor = Color.FromArgb(163, 21, 21); // Red
				txt.Styles[Style.Cpp.Character].ForeColor = Color.FromArgb(163, 21, 21); // Red
				txt.Styles[Style.Cpp.Preprocessor].ForeColor = Color.FromArgb(128, 128, 128); // Gray
				txt.Styles[Style.Cpp.Operator].ForeColor = Color.FromArgb(0, 0, 120); // Dark Blue
				txt.Styles[Style.Cpp.Regex].ForeColor = IntToColor(0xff00ff);
				txt.Styles[Style.Cpp.Word].ForeColor = Color.Blue;
				txt.Styles[Style.Cpp.Word2].ForeColor = Color.FromArgb(52, 146, 184); // Turqoise
				txt.SelectionBackColor = Color.FromArgb(153, 201, 239);
			}

			//Extras.
			txt.Styles[Style.Cpp.CommentDocKeyword].ForeColor = IntToColor(0xB3D991);
			txt.Styles[Style.Cpp.CommentDocKeywordError].ForeColor = IntToColor(0xFF0000);
			txt.Styles[Style.Cpp.GlobalClass].ForeColor = IntToColor(0x48A8EE);
			txt.Styles[Style.Cpp.Verbatim].ForeColor = Color.FromArgb(163, 21, 21); // Red
			txt.Styles[Style.Cpp.StringEol].BackColor = Color.Pink;
			txt.LexerName = "cpp";
			//txt.LexerName = "";
			txt.SetKeywords(0, keywords1);
			txt.SetKeywords(1, keywords2);
		}

		private void InitDragDropFile()
		{
			txtIn.AllowDrop = true;
			txtIn.DragEnter += TxtIn_DragEnter;
			txtIn.DragDrop += TxtIn_DragDrop;
		}

		private void InitHotkeys()
		{
			// register the hotkeys with the form
			HotKeyManager.AddHotKey(this, OpenSearch, Keys.F, true);
			HotKeyManager.AddHotKey(this, OpenFindDialog, Keys.F, true, false, true);
			HotKeyManager.AddHotKey(this, OpenReplaceDialog, Keys.R, true);
			HotKeyManager.AddHotKey(this, OpenReplaceDialog, Keys.H, true);
			HotKeyManager.AddHotKey(this, Uppercase, Keys.U, true);
			HotKeyManager.AddHotKey(this, Lowercase, Keys.L, true);
			HotKeyManager.AddHotKey(this, ZoomIn, Keys.Oemplus, true);
			HotKeyManager.AddHotKey(this, ZoomOut, Keys.OemMinus, true);
			HotKeyManager.AddHotKey(this, ZoomDefault, Keys.D0, true);
			HotKeyManager.AddHotKey(this, CloseSearch, Keys.Escape);
			HotKeyManager.AddHotKey(this, RunStopScript, Keys.F9);
			//Remove conflicting hotkeys from scintilla.
			txtIn.ClearCmdKey(Keys.Control | Keys.F);
			txtIn.ClearCmdKey(Keys.Control | Keys.R);
			txtIn.ClearCmdKey(Keys.Control | Keys.H);
			txtIn.ClearCmdKey(Keys.Control | Keys.L);
			txtIn.ClearCmdKey(Keys.Control | Keys.U);
		}

		private void InvokeIfNeeded(Action action)
		{
			if (InvokeRequired)
			{
				_ = BeginInvoke(action);
			}
			else
			{
				action.Invoke();
			}
		}

		private void Keyview_FormClosing(object sender, FormClosingEventArgs e)
		{
			if (!ConfirmDiscardChanges())
			{
				e.Cancel = true;
				return;
			}

			timer.Stop();
			//Script.Stop();
			AutosaveScratchDocument();
		}

		private void AutosaveScratchDocument()
        {
			if (!document.IsScratch)
				return;

			var dir = Path.GetDirectoryName(lastrun);
			try
			{
				if (!Directory.Exists(dir))
					_ = Directory.CreateDirectory(dir);

				File.WriteAllText(lastrun, txtIn.Text);
			}
			catch (Exception ex)
			{
				documentStatusLabel.Text = $"Scratch autosave failed: {ex.Message}";
			}
        }

		private void Keyview_Load(object sender, EventArgs e)
		{
			InitColors(txtIn);
			InitColors(txtOut);
			InitSyntaxColoring(txtIn);//Keysharp syntax for txtIn.
			//InitSyntaxColoring(txtOut);
			txtOut.StyleResetDefault();
			txtOut.Styles[Style.Default].Font = "Consolas";
			txtOut.Styles[Style.Default].Size = 10;
			//txt.Styles[Style.Default].BackColor = IntToColor(0xFFFCE1);
			//txt.Styles[Style.Default].BackColor = IntToColor(0x212121);
			//txt.Styles[Style.Default].ForeColor = IntToColor(0xFFFFFF);
			txtOut.StyleClearAll();
			csStyler.ApplyStyle(txtOut);//C# syntax for txtOut.
			csStyler.SetKeywords(txtOut);
			InitNumberMargin(txtIn);
			InitNumberMargin(txtOut);
			//InitBookmarkMargin();
			InitCodeFolding(txtIn);
			InitCodeFolding(txtOut);
			InitDragDropFile();
			InitHotkeys();
			//txtIn.StyleNeeded += TxtIn_StyleNeeded;

			timer.Interval = 1000;
			timer.Tick += Timer_Tick;
			timer.Start();
		}

		private void Keyview_ResizeEnd(object sender, EventArgs e) => splitContainer.SplitterDistance = Width / 2;

		private void LoadDataFromFile(string path)
		{
			if (!File.Exists(path))
				return;

			suppressDocumentChange = true;
			try
			{
				var fullPath = Path.GetFullPath(path);
				var text = File.ReadAllText(fullPath);
				FileName.Text = Path.GetFileName(fullPath);
				txtIn.Text = text;
				document.LoadFile(fullPath, text);
				txtIn.EmptyUndoBuffer();
			}
			finally
			{
				suppressDocumentChange = false;
			}

			lastKeyTime = DateTime.UtcNow;
			UpdateDocumentUi();
		}

		private void LoadScratchDocument()
		{
			suppressDocumentChange = true;
			try
			{
				var text = File.Exists(lastrun) ? File.ReadAllText(lastrun) : "";
				txtIn.Text = text;
				document.LoadScratch();
				txtIn.EmptyUndoBuffer();
			}
			finally
			{
				suppressDocumentChange = false;
			}

			lastKeyTime = DateTime.UtcNow;
			UpdateDocumentUi();
		}

		private void Lowercase()
		{
			var start = txtIn.SelectionStart;
			var end = txtIn.SelectionEnd;
			txtIn.ReplaceSelection(txtIn.GetTextRange(start, end - start).ToLower());
			txtIn.SetSelection(start, end);
			lastKeyTime = DateTime.UtcNow;
		}

		private void lowercaseSelectionToolStripMenuItem_Click(object sender, EventArgs e) => Lowercase();

		private void OpenFindDialog()
		{
		}

		private void OpenReplaceDialog()
		{
		}

		private void OpenSearch()
		{
			SearchManager.SearchBox = TxtSearch;
			SearchManager.TextArea = txtIn;

			if (!SearchIsOpen)
			{
				SearchIsOpen = true;
				InvokeIfNeeded(delegate ()
				{
					PanelSearch.Visible = true;
					TxtSearch.Text = SearchManager.LastSearch;
					_ = TxtSearch.Focus();
					TxtSearch.SelectAll();
				});
			}
			else
			{
				InvokeIfNeeded(delegate ()
				{
					_ = TxtSearch.Focus();
					TxtSearch.SelectAll();
				});
			}
		}

		private void openToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (ConfirmDiscardChanges() && openFileDialog.ShowDialog() == DialogResult.OK)
			{
				LoadDataFromFile(openFileDialog.FileName);
			}
		}

		private bool ConfirmDiscardChanges()
		{
			if (!document.IsDirty(txtIn.Text))
				return true;

			var result = MessageBox.Show(
				$"Save changes to {document.DisplayName}?",
				"Keyview",
				MessageBoxButtons.YesNoCancel,
				MessageBoxIcon.Question);

			return result switch
			{
				DialogResult.Yes => SaveDocument(),
				DialogResult.No => true,
				_ => false
			};
		}

		private bool SaveDocument()
		{
			if (document.IsScratch)
				return false;

			try
			{
				File.WriteAllText(document.CurrentFilePath, txtIn.Text);
				document.MarkSaved(txtIn.Text);
				UpdateDocumentUi();
				return true;
			}
			catch (Exception ex)
			{
				_ = MessageBox.Show($"Unable to save file: {ex.Message}", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return false;
			}
		}

		private void CompileDocument()
		{
			if (!document.CanCompile || (document.IsDirty(txtIn.Text) && !SaveDocument()))
				return;

			btnCompileScript.Enabled = false;
			tslCodeStatus.Text = "Writing .cks...";
			Refresh();

			if (KeyviewDocumentCompiler.TryCompile(document.CurrentFilePath, ch, out var outputPath, out var error))
			{
				tslCodeStatus.ForeColor = Color.Green;
				tslCodeStatus.Text = $"Wrote {outputPath}";
			}
			else
			{
				tslCodeStatus.ForeColor = Color.Red;
				tslCodeStatus.Text = "Compile failed";
				SetTxtOut(error);
			}

			UpdateDocumentUi();
		}

		private void UpdateDocumentUi()
		{
			var dirty = document.IsDirty(txtIn.Text);
			Text = document.GetWindowTitle(baseTitle, dirty);
			documentStatusLabel.Text = document.GetStatusText(dirty);
			saveToolStripMenuItem.Enabled = !document.IsScratch && dirty;
			compileToolStripMenuItem.Enabled = document.CanCompile;
			btnCompileScript.Visible = !document.IsScratch;
			btnCompileScript.Enabled = document.CanCompile;
		}

		private void Outdent()
		{
			// we use this hack to send "Shift+Tab" to scintilla, since there is no known API to outdent,
			// although the indentation function exists. Pressing Shift+Tab with the editor focused confirms this.
			GenerateKeystrokes("+{TAB}");
			lastKeyTime = DateTime.UtcNow;
		}

		private void outdentSelectionToolStripMenuItem_Click(object sender, EventArgs e) => Outdent();

		private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
		{
			txtIn.Paste();
			lastKeyTime = DateTime.UtcNow;
		}

		private void selectAllToolStripMenuItem_Click(object sender, EventArgs e) => txtIn.SelectAll();

		private void saveToolStripMenuItem_Click(object sender, EventArgs e) => SaveDocument();

		private void compileToolStripMenuItem_Click(object sender, EventArgs e) => CompileDocument();

		private void selectLineToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (string.IsNullOrEmpty(txtIn.Text))
				return;
			Line line = txtIn.Lines[txtIn.CurrentLine];
			txtIn.SetSelection(line.Position + line.Length, line.Position);
		}

		private void SetFailure()
		{
			tslCodeStatus.ForeColor = Color.Red;
			tslCodeStatus.Text = "Error";
			SetTxtOut("");
			Refresh();
		}

		private void SetStart()
		{
			fullCode = trimmedCode = "";
			tslCodeStatus.ForeColor = Color.Black;
			tslCodeStatus.Text = "";
			//Don't clear txtOut, it causes flicker.
			Refresh();
		}

		private void SetSuccess(double seconds)
		{
			tslCodeStatus.ForeColor = Color.Green;
			tslCodeStatus.Text = $"Ok ({seconds:F1}s)";
			Refresh();
		}

		private void SetTxtOut(string txt)
		{
			txtOut.ReadOnly = false;
			txtOut.Text = txt;
			txtOut.ReadOnly = true;
			UpdateNumberMarginWidth(txtOut);
		}

		private void splitContainer_DoubleClick(object sender, EventArgs e) => splitContainer.SplitterDistance = Width / 2;

		//private void TextArea_MarginClick(object sender, MarginClickEventArgs e)
		//{
		//  if (e.Margin == BOOKMARK_MARGIN)
		//  {
		//      // Do we have a marker for this line?
		//      const uint mask = 1 << BOOKMARK_MARKER;
		//      var line = txtIn.Lines[txtIn.LineFromPosition(e.Position)];

		//      if ((line.MarkerGet() & mask) > 0)
		//      {
		//          // Remove existing bookmark
		//          line.MarkerDelete(BOOKMARK_MARKER);
		//      }
		//      else
		//      {
		//          // Add bookmark
		//          line.MarkerAdd(BOOKMARK_MARKER);
		//      }
		//  }
		//}

		private async void Timer_Tick(object sender, EventArgs e)
		{
			if (!isCompiling && (force || ((DateTime.UtcNow - lastKeyTime).TotalSeconds >= updateFreqSeconds && lastKeyTime > lastCompileTime)) && txtIn.Text != "")
			{
				timer.Enabled = false;
				isCompiling = true;
				lastCompileTime = DateTime.UtcNow;
				var oldIndex = txtOut.FirstVisibleLine;

				try
				{
					await KeyviewCompilerRunner.RunCompile(
						txtIn.Text,
						ch,
						code => fullCode = code,
						code => trimmedCode = code,
						trimend,
						trimstr,
						SetStart,
						SetSuccess,
						SetFailure,
						text => tslCodeStatus.Text = text,
						Refresh,
						SetTxtOut,
						() => chkFullCode.Checked,
						() => btnRunScript.Enabled = false,
						() => btnRunScript.Enabled = true,
						AutosaveScratchDocument,
						() => oldIndex = txtOut.FirstVisibleLine,
						() => txtOut.FirstVisibleLine = oldIndex);
				}
				finally
				{
					isCompiling = false;
					timer.Enabled = true;
				}
			}

			if (force)
				force = false;
		}

		private void RunScript_Click(object sender, EventArgs e) => RunStopScript();

		private void RunStopScript()
		{
			if (scriptProcess != null)
			{
				scriptProcess.Kill();
				scriptProcess = null;
			}

			if (btnRunScript.Text == btnRunScriptText["Stop"])
				return;

			if (CompilerHelper.compiledasm == null)
			{
				_ = MessageBox.Show("Please wait, code is still compiling...", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}

			scriptProcess = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = GetKeysharpExecutable(),
					Arguments = "--assembly *",
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			scriptProcess.EnableRaisingEvents = true;
			scriptProcess.Exited += (object sender, EventArgs e) =>
			{
				toolStrip1.Invoke((() =>
				{
					btnRunScript.Text = btnRunScriptText["Run"];
				}));
				scriptProcess = null;
			};
			_ = scriptProcess.Start();

			// Write the raw assembly bytes and close stdin; the child ("--assembly *") reads to EOF.
			using (var stdin = scriptProcess.StandardInput.BaseStream)
			{
				stdin.Write(CompilerHelper.compiledBytes, 0, CompilerHelper.compiledBytes.Length);
				stdin.Flush();
			}

			btnRunScript.Text = btnRunScriptText["Stop"];
		}

		private static string GetKeysharpExecutable() => "Keysharp.exe";

		private void TxtIn_DragDrop(object sender, DragEventArgs e)
		{
			var data = e.Data.GetData(DataFormats.FileDrop);

			if (data is string[] filenames)
			{
				try
				{
					if (filenames.Length > 0)
					{
						if (ConfirmDiscardChanges())
							LoadDataFromFile(filenames[0]);
					}
				}
				catch (Exception ex)
				{
					_ = Dialogs.MsgBox($"Unable to load file: {ex.Message}");
				}
			}
		}

		private void TxtIn_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effect = DragDropEffects.Copy;
			else
				e.Effect = DragDropEffects.None;
		}

		private void txtIn_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.F5)
				force = true;
			else if (ReferenceEquals(sender, txtIn))
				lastKeyTime = DateTime.UtcNow;
		}

		private void txtIn_MarginClick(object sender, MarginClickEventArgs e)
		{
			var txt = sender as Scintilla;

			if (e.Margin == BOOKMARK_MARGIN)
			{
				// Do we have a marker for this line?
				const uint mask = 1 << BOOKMARK_MARKER;
				var line = txt.Lines[txt.LineFromPosition(e.Position)];

				if ((line.MarkerGet() & mask) > 0)
				{
					// Remove existing bookmark
					line.MarkerDelete(BOOKMARK_MARKER);
				}
				else
				{
					// Add bookmark
					_ = line.MarkerAdd(BOOKMARK_MARKER);
				}
			}
		}

		private void txtIn_TextChanged(object sender, EventArgs e)
		{
			UpdateNumberMarginWidth(txtIn);
			lastKeyTime = DateTime.UtcNow;

			if (!suppressDocumentChange)
			{
				UpdateDocumentUi();
				AutosaveScratchDocument();
			}
		}

		private void txtOut_TextChanged(object sender, EventArgs e)
		{
			UpdateNumberMarginWidth(txtOut);
		}

		private void txtOut_KeyDown(object sender, KeyEventArgs e) => txtIn_KeyDown(sender, e);

		private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
		{
			if (HotKeyManager.IsHotkey(e, Keys.Enter))
			{
				SearchManager.Find(true, false);
			}

			if (HotKeyManager.IsHotkey(e, Keys.Enter, true) || HotKeyManager.IsHotkey(e, Keys.Enter, false, true))
			{
				SearchManager.Find(false, false);
			}
		}

		private void TxtSearch_TextChanged(object sender, EventArgs e) => SearchManager.Find(true, true);

		private void Uppercase()
		{
			var start = txtIn.SelectionStart;
			var end = txtIn.SelectionEnd;
			txtIn.ReplaceSelection(txtIn.GetTextRange(start, end - start).ToUpper());
			txtIn.SetSelection(start, end);
			lastKeyTime = DateTime.UtcNow;
		}

		private void uppercaseSelectionToolStripMenuItem_Click(object sender, EventArgs e) => Uppercase();

		private void wordWrapToolStripMenuItem1_Click(object sender, EventArgs e)
		{
			wordWrapItem.Checked = !wordWrapItem.Checked;
			txtOut.WrapMode = txtIn.WrapMode = wordWrapItem.Checked ? WrapMode.Word : WrapMode.None;
		}

		private void zoom100ToolStripMenuItem_Click(object sender, EventArgs e) => ZoomDefault();

		private void ZoomDefault()
		{
			txtIn.Zoom = 0;
			txtOut.Zoom = 0;
		}

		private void ZoomIn()
		{
			txtIn.ZoomIn();
			txtOut.ZoomIn();
			UpdateNumberMarginWidth(txtIn);
			UpdateNumberMarginWidth(txtOut);
		}

		private void zoomInToolStripMenuItem_Click(object sender, EventArgs e) => ZoomIn();

		private void ZoomOut()
		{
			txtIn.ZoomOut();
			txtOut.ZoomOut();
			UpdateNumberMarginWidth(txtIn);
			UpdateNumberMarginWidth(txtOut);
		}

		private void zoomOutToolStripMenuItem_Click(object sender, EventArgs e) => ZoomOut();
	}
#endif

#if !WINDOWS
	internal sealed class Keyview : Eto.Forms.Form
	{
		private readonly struct TextSnapshot
		{
			public TextSnapshot(string text, int selectionStart, int selectionLength)
			{
				Text = text ?? "";
				SelectionStart = selectionStart;
				SelectionLength = selectionLength;
			}

			public string Text { get; }
			public int SelectionStart { get; }
			public int SelectionLength { get; }
		}

		private readonly Stack<TextSnapshot> undoStack = new ();
		private readonly Stack<TextSnapshot> redoStack = new ();
		private readonly TextArea inputArea = new ();
		private readonly TextArea outputArea = new ();
		private readonly TextBox searchBox = new ();
		private readonly Button nextSearchButton = new () { Text = "Next" };
		private readonly Button prevSearchButton = new () { Text = "Prev" };
		private readonly Button closeSearchButton = new () { Text = "Close" };
		private readonly CheckBox fullCodeCheck = new () { Text = "Full code" };
		private readonly Button copyFullCodeButton = new () { Text = "Copy full code" };
		private readonly Button runScriptButton = new () { Text = "▶ Run script (F9)" };
		private readonly Button compileScriptButton = new () { Text = "Compile .cks" };
		private readonly Label codeStatusLabel = new () { Text = "" };
		private readonly Label documentStatusLabel = new () { Text = "" };
		private readonly Panel searchPanel = new ();
		private readonly ButtonMenuItem undoMenuItem = new () { Text = "&Undo" };
		private readonly ButtonMenuItem redoMenuItem = new () { Text = "&Redo" };
#if OSX
		private readonly ButtonMenuItem saveMenuItem = new () { Text = "&Save", Shortcut = Eto.Forms.Keys.Application | Eto.Forms.Keys.S };
#else
		private readonly ButtonMenuItem saveMenuItem = new () { Text = "&Save", Shortcut = Eto.Forms.Keys.Control | Eto.Forms.Keys.S };
#endif
		private readonly ButtonMenuItem compileMenuItem = new () { Text = "&Compile .cks" };
		private Splitter editorSplitter;
		private readonly UITimer timer = new ();
		private readonly CompilerHelper ch = new ();
		private readonly char[] trimend = ['\n', '\r'];
		private readonly string trimstr = "{}\t";
		private readonly double updateFreqSeconds = 1;
		private readonly string lastrun;
		private readonly KeyviewDocumentState document = new ();
		private readonly Dictionary<string, string> runScriptText = new ()
		{
			{ "Run", "▶ Run script (F9)" },
			{ "Stop", "◾️ Stop script (F9)" }
		};

		private bool force;
		private bool isCompiling;
		private string fullCode = "";
		private DateTime lastCompileTime = DateTime.UtcNow;
		private DateTime lastKeyTime = DateTime.UtcNow;
		private bool searchIsOpen;
		private string trimmedCode = "";
		private Process scriptProcess;
		private string lastSearch = "";
		private int lastSearchIndex;
		private bool suppressUndo;
		private string lastText = "";
		private int lastSelectionStart;
		private int lastSelectionLength;
		private string baseTitle;
		private bool suppressDocumentChange;

		public Keyview(string initialFile = null)
		{
			lastrun = $"{Accessors.A_AppData}/Keysharp/lastkeyviewrun.txt";
			Title = $"Keyview {Assembly.GetExecutingAssembly().GetName().Version}";
			baseTitle = Title;
			InitializeWindowIcon();
			ShowInTaskbar = true;
			Resizable = true;
			Minimizable = true;
			Maximizable = true;
			WindowStyle = WindowStyle.Default;
			ClientSize = new Eto.Drawing.Size(1400, 900);

			InitializeMenu();
			InitializeEditors();
			InitializeSearchPanel();
			InitializeStatusBar();
			InitializeLayout();

			timer.Interval = updateFreqSeconds;
			timer.Elapsed += Timer_Elapsed;
			timer.Start();

			Shown += (_, _) =>
			{
				InitializeWindowIcon();
				FitToScreen();
				if (editorSplitter != null)
					editorSplitter.Position = Math.Max(200, ClientSize.Width / 2);
			};

			Closing += (_, e) => e.Cancel = !ConfirmDiscardChanges();
			Closed += (_, _) =>
			{
				timer.Stop();
				AutosaveScratchDocument();
				try
				{
					scriptProcess?.Kill();
				}
				catch
				{
				}
#if OSX
				Eto.Mac.AppDelegate.FileOpened -= MacFileOpened;
				Application.Instance.Quit();
#endif
			};

			if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
			{
				LoadDataFromFile(initialFile);
			}
			else if (File.Exists(lastrun))
			{
				LoadScratchDocument();
			}
			else
				UpdateDocumentUi();

#if OSX
			Eto.Mac.AppDelegate.FileOpened += MacFileOpened;
#endif
		}

		private void InitializeMenu()
		{
			var fileMenu = new ButtonMenuItem { Text = "&File" };
			var openItem = new ButtonMenuItem { Text = "&Open..." };
			openItem.Click += (_, _) => OpenFile();
			saveMenuItem.Click += (_, _) => SaveDocument();
			compileMenuItem.Click += (_, _) => CompileDocument();
			fileMenu.Items.AddRange(new MenuItem[] { openItem, saveMenuItem, compileMenuItem });

			var editMenu = new ButtonMenuItem { Text = "&Edit" };
			undoMenuItem.Click += (_, _) => Undo();
			redoMenuItem.Click += (_, _) => Redo();
			var cutItem = new ButtonMenuItem { Text = "Cu&t" };
			cutItem.Click += (_, _) => { CutSelection(); lastKeyTime = DateTime.UtcNow; };
			var copyItem = new ButtonMenuItem { Text = "&Copy" };
			copyItem.Click += (_, _) => { CopySelection(); lastKeyTime = DateTime.UtcNow; };
			var pasteItem = new ButtonMenuItem { Text = "&Paste" };
			pasteItem.Click += (_, _) => { PasteFromClipboard(); lastKeyTime = DateTime.UtcNow; };
			var selectLineItem = new ButtonMenuItem { Text = "Select &Line" };
			selectLineItem.Click += (_, _) => SelectLine();
			var selectAllItem = new ButtonMenuItem { Text = "Select &All" };
			selectAllItem.Click += (_, _) => inputArea.SelectAll();
			var clearSelectionItem = new ButtonMenuItem { Text = "Clea&r Selection" };
			clearSelectionItem.Click += (_, _) =>
			{
				var (start, _) = GetSelection();
				SetSelection(start, 0);
				lastKeyTime = DateTime.UtcNow;
			};
			var indentItem = new ButtonMenuItem { Text = "&Indent" };
			indentItem.Click += (_, _) => AdjustIndent(false);
			var outdentItem = new ButtonMenuItem { Text = "&Outdent" };
			outdentItem.Click += (_, _) => AdjustIndent(true);
			var uppercaseItem = new ButtonMenuItem { Text = "&Uppercase" };
			uppercaseItem.Click += (_, _) => TransformSelection(s => s.ToUpperInvariant());
			var lowercaseItem = new ButtonMenuItem { Text = "&Lowercase" };
			lowercaseItem.Click += (_, _) => TransformSelection(s => s.ToLowerInvariant());
			editMenu.Items.AddRange(new MenuItem[]
			{
				undoMenuItem, redoMenuItem, new SeparatorMenuItem(),
				cutItem, copyItem, pasteItem, new SeparatorMenuItem(),
				selectLineItem, selectAllItem, clearSelectionItem, new SeparatorMenuItem(),
				indentItem, outdentItem, new SeparatorMenuItem(),
				uppercaseItem, lowercaseItem
			});

			var searchMenu = new ButtonMenuItem { Text = "&Search" };
			var findItem = new ButtonMenuItem { Text = "&Quick Find..." };
			findItem.Click += (_, _) => OpenSearch();
			searchMenu.Items.Add(findItem);

			var viewMenu = new ButtonMenuItem { Text = "&View" };
			var wordWrapItem = new CheckMenuItem { Text = "&Word Wrap", Checked = true };
			wordWrapItem.Click += (_, _) => SetWordWrap(wordWrapItem.Checked == true);
			var zoomInItem = new ButtonMenuItem { Text = "Zoom &In" };
			zoomInItem.Click += (_, _) => ZoomIn();
			var zoomOutItem = new ButtonMenuItem { Text = "Zoom &Out" };
			zoomOutItem.Click += (_, _) => ZoomOut();
			var zoomDefaultItem = new ButtonMenuItem { Text = "&Zoom 100%" };
			zoomDefaultItem.Click += (_, _) => ZoomDefault();
			viewMenu.Items.AddRange(new MenuItem[]
			{
				wordWrapItem, new SeparatorMenuItem(),
				zoomInItem, zoomOutItem, zoomDefaultItem
			});

			Menu = new Eto.Forms.MenuBar
			{
				Items = { fileMenu, editMenu, searchMenu, viewMenu }
			};
		}

		private void InitializeEditors()
		{
#if OSX
			var font = TryMonospaceFont(13);
#else
			var font = TryMonospaceFont(10);
#endif
			inputArea.Font = font;
			outputArea.Font = font;
			inputArea.TextReplacements = TextReplacements.None;
			outputArea.ReadOnly = true;
			inputArea.Wrap = true;
			outputArea.Wrap = true;
			inputArea.TextChanged += InputArea_TextChanged;
#if LINUX
			KeyDown += InputArea_KeyDown;
#else
			inputArea.KeyDown += InputArea_KeyDown;
			outputArea.KeyDown += InputArea_KeyDown;
#endif
			inputArea.MouseUp += (_, _) => UpdateSelectionSnapshot();
		}

		private static Font TryMonospaceFont(float size)
		{
			var candidates = new[]
			{
				"Consolas",
				"JetBrains Mono",
				"Fira Code",
				"DejaVu Sans Mono",
				"Liberation Mono",
				"Monospace"
			};

			foreach (var name in candidates)
			{
				try
				{
					return new Font(name, size);
				}
				catch
				{
				}
			}

			return SystemFonts.Default(size);
		}

		private void InitializeSearchPanel()
		{
			searchBox.Width = 280;
			searchBox.TextChanged += (_, _) => UpdateSearchText();
			searchBox.KeyDown += SearchBox_KeyDown;
			nextSearchButton.Click += (_, _) => Find(true, false);
			prevSearchButton.Click += (_, _) => Find(false, false);
			closeSearchButton.Click += (_, _) => CloseSearch();
			searchPanel.Content = new StackLayout
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				Items = { searchBox, prevSearchButton, nextSearchButton, closeSearchButton }
			};
			searchPanel.Visible = false;
		}

		private void InitializeStatusBar()
		{
			fullCodeCheck.CheckedChanged += (_, _) => UpdateOutputFromCache();
			copyFullCodeButton.Click += (_, _) => CopyFullCode();
			runScriptButton.Click += (_, _) => RunStopScript();
			runScriptButton.Enabled = false;
			compileScriptButton.Click += (_, _) => CompileDocument();
			documentStatusLabel.Width = 500;
			codeStatusLabel.Text = "";
		}

		private void InitializeLayout()
		{
			editorSplitter = new Splitter
			{
				Orientation = Orientation.Horizontal,
				SplitterWidth = 2,
				FixedPanel = SplitterFixedPanel.None,
				RelativePosition = 0.5,
				Panel1MinimumSize = 200,
				Panel2MinimumSize = 200,
				Panel1 = new Panel { Content = inputArea },
				Panel2 = new Panel { Content = outputArea }
			};

			var statusRow = new StackLayout
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8,
				Items =
				{
					documentStatusLabel,
					new StackLayoutItem(new Panel()) { Expand = true },
					new Label { Text = "Code compile:" },
					codeStatusLabel,
					fullCodeCheck,
					copyFullCodeButton,
					compileScriptButton,
					runScriptButton
				}
			};

			var statusContainer = new Panel
			{
				Padding = new Padding(8, 6, 8, 6),
				Content = statusRow
			};

			Content = new TableLayout
			{
				Spacing = new Eto.Drawing.Size(0, 0),
				Rows =
				{
					new TableRow(searchPanel),
					new TableRow(editorSplitter) { ScaleHeight = true },
					new TableRow(statusContainer)
				}
			};
		}

		private void InitializeWindowIcon()
		{
			var icon = TryLoadIcon();
			if (icon != null)
				Icon = icon;
		}

		private static Icon TryLoadIcon()
		{
			foreach (var candidate in EnumerateIconPaths("Keysharp.png"))
			{
				if (!File.Exists(candidate))
					continue;

				try
				{
					return new Icon(1f, new Bitmap(candidate));
				}
				catch
				{
				}
			}

			foreach (var candidate in EnumerateIconPaths("Keysharp.ico"))
			{
				if (!File.Exists(candidate))
					continue;

				try
				{
					return new Icon(candidate);
				}
				catch
				{
					// Gtk cannot load some compressed .ico files; ignore.
				}
			}

			return null;
		}

		private static IEnumerable<string> EnumerateIconPaths(string fileName)
		{
			var candidates = new List<DirectoryInfo>();
			var appBase = new DirectoryInfo(AppContext.BaseDirectory);
			candidates.Add(appBase);
			if (!string.IsNullOrEmpty(Environment.CurrentDirectory))
				candidates.Add(new DirectoryInfo(Environment.CurrentDirectory));
			if (!string.IsNullOrEmpty(Environment.ProcessPath))
				candidates.Add(new DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath)));

			foreach (var baseDir in candidates.Where(dir => dir.Exists))
			{
				for (var current = baseDir; current != null; current = current.Parent)
				{
					yield return Path.Combine(current.FullName, fileName);
					yield return Path.Combine(current.FullName, "Keysharp", fileName);
				}
			}
		}

		private void FitToScreen()
		{
			var screen = Eto.Forms.Screen.PrimaryScreen;
			if (screen == null)
				return;

			RectangleF area = new RectangleF(0, 0, 1200, 800);
			try {
				area = screen.WorkingArea;
			} catch { }
			var width = (int)Math.Min(ClientSize.Width, area.Width);
			var height = (int)Math.Min(ClientSize.Height, area.Height);
			ClientSize = new Eto.Drawing.Size(width, height);
			Location = new Point(
				(int)area.X + Math.Max(0, (int)(area.Width - Size.Width) / 2),
				(int)area.Y + Math.Max(0, (int)(area.Height - Size.Height) / 2));
		}

		private void InputArea_TextChanged(object sender, EventArgs e)
		{
			RecordUndoSnapshot();
			lastKeyTime = DateTime.UtcNow;

			if (!suppressDocumentChange)
			{
				UpdateDocumentUi();
				AutosaveScratchDocument();
			}
		}

		private void InputArea_KeyDown(object sender, KeyEventArgs e)
		{
			var inputIsTarget = ReferenceEquals(sender, inputArea) || inputArea.HasFocus;

			if (inputIsTarget)
				UpdateSelectionSnapshot();

			if (e.Key == Keys.F5)
			{
				force = true;
				return;
			}

			if (e.Key == Keys.F9)
			{
				RunStopScript();
				e.Handled = true;
				return;
			}

			if (e.Key == Keys.Escape && searchIsOpen)
			{
				CloseSearch();
				e.Handled = true;
				return;
			}

			if (inputIsTarget)
			{
				if (e.Key == Keys.Z && e.Modifiers == Keys.Control)
				{
					Undo();
					e.Handled = true;
					return;
				}

				if (e.Key == Keys.Y && e.Modifiers == Keys.Control)
				{
					Redo();
					e.Handled = true;
					return;
				}

				if (e.Key == Keys.Z && e.Modifiers == (Keys.Control | Keys.Shift))
				{
					Redo();
					e.Handled = true;
					return;
				}
			}

			if (e.Modifiers == Keys.Control)
			{
				switch (e.Key)
				{
					case Keys.F:
						OpenSearch();
						e.Handled = true;
						break;
					case Keys.U:
						TransformSelection(s => s.ToUpperInvariant());
						e.Handled = true;
						break;
					case Keys.L:
						TransformSelection(s => s.ToLowerInvariant());
						e.Handled = true;
						break;
					case Keys.Equal:
						ZoomIn();
						e.Handled = true;
						break;
					case Keys.Minus:
						ZoomOut();
						e.Handled = true;
						break;
					case Keys.D0:
						ZoomDefault();
						e.Handled = true;
						break;
				}
			}
		}

		private void SearchBox_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Keys.Enter && e.Modifiers == Keys.None)
			{
				Find(true, false);
				e.Handled = true;
				return;
			}

			if (e.Key == Keys.Enter && (e.Modifiers == Keys.Shift || e.Modifiers == Keys.Control))
			{
				Find(false, false);
				e.Handled = true;
			}
		}

		private void CopySelection()
		{
			var selection = inputArea.Selection;
			if (selection.Length() <= 0)
				return;
			Clipboard.Instance.Text = inputArea.Text?.Substring(selection.Start, selection.Length()) ?? "";
		}

		private void CutSelection()
		{
			var selection = inputArea.Selection;
			if (selection.Length() <= 0)
				return;
			CopySelection();
			var text = inputArea.Text ?? "";
			inputArea.Text = text.Remove(selection.Start, selection.Length());
			SetSelection(selection.Start, 0);
		}

		private void PasteFromClipboard()
		{
			var clip = Clipboard.Instance.Text ?? "";
			if (clip.Length == 0)
				return;
			var (start, length) = GetSelection();
			var text = inputArea.Text ?? "";
			var before = text.Substring(0, start);
			var after = text.Substring(start + length);
			inputArea.Text = before + clip + after;
			SetSelection(start + clip.Length, 0);
		}

			private (int start, int length) GetSelection()
			{
				var selection = inputArea.Selection;
				var start = Math.Max(0, selection.Start);
				var end = Math.Max(0, selection.End);
			if (end < start)
				(end, start) = (start, end);
			return (start, end - start);
		}

			private void SetSelection(int start, int length)
			{
				var safeStart = Math.Max(0, start);
				var safeLength = Math.Max(0, length);
				// Eto.Range is inclusive at both ends; use start+length-1 for a length-based selection.
				var end = safeLength > 0 ? safeStart + safeLength - 1 : safeStart - 1;
				inputArea.Selection = new Range<int>(safeStart, end);
			}

		private void UpdateSelectionSnapshot()
		{
			var (start, length) = GetSelection();
			lastSelectionStart = start;
			lastSelectionLength = length;
		}

		private TextSnapshot CaptureSnapshot()
		{
			var (start, length) = GetSelection();
			return new TextSnapshot(inputArea.Text ?? "", start, length);
		}

		private void ApplySnapshot(TextSnapshot snapshot)
		{
			suppressUndo = true;
			try
			{
				inputArea.Text = snapshot.Text ?? "";
				var textLength = inputArea.Text?.Length ?? 0;
				var start = Math.Min(Math.Max(0, snapshot.SelectionStart), textLength);
				var length = Math.Min(Math.Max(0, snapshot.SelectionLength), Math.Max(0, textLength - start));
				SetSelection(start, length);
				lastText = inputArea.Text ?? "";
				lastSelectionStart = start;
				lastSelectionLength = length;
			}
			finally
			{
				suppressUndo = false;
			}

			lastKeyTime = DateTime.UtcNow;
			UpdateUndoRedoState();
		}

		private void RecordUndoSnapshot()
		{
			if (suppressUndo)
				return;

			var currentText = inputArea.Text ?? "";
			if (currentText == lastText)
				return;

			undoStack.Push(new TextSnapshot(lastText, lastSelectionStart, lastSelectionLength));
			redoStack.Clear();
			lastText = currentText;
			UpdateSelectionSnapshot();
			UpdateUndoRedoState();
		}

		private void ResetUndoHistory()
		{
			undoStack.Clear();
			redoStack.Clear();
			lastText = inputArea.Text ?? "";
			UpdateSelectionSnapshot();
			UpdateUndoRedoState();
		}

		private void UpdateUndoRedoState()
		{
			var canUndo = undoStack.Count > 0;
			var canRedo = redoStack.Count > 0;
			undoMenuItem.Enabled = canUndo;
			redoMenuItem.Enabled = canRedo;
		}

		private void Undo()
		{
			if (undoStack.Count == 0)
				return;

			redoStack.Push(CaptureSnapshot());
			ApplySnapshot(undoStack.Pop());
		}

		private void Redo()
		{
			if (redoStack.Count == 0)
				return;

			undoStack.Push(CaptureSnapshot());
			ApplySnapshot(redoStack.Pop());
		}

		private void OpenSearch()
		{
			if (!searchIsOpen)
			{
				searchIsOpen = true;
				searchPanel.Visible = true;
				searchBox.Text = lastSearch;
			}

			searchBox.Focus();
			searchBox.SelectAll();
		}

		private void CloseSearch()
		{
			searchIsOpen = false;
			searchPanel.Visible = false;
		}

		private void UpdateSearchText()
		{
			lastSearch = searchBox.Text ?? "";
			lastSearchIndex = 0;
		}

		private void Find(bool next, bool incremental)
		{
			var needle = searchBox.Text ?? "";
			lastSearch = needle;

			if (string.IsNullOrEmpty(needle))
				return;

			var text = inputArea.Text ?? "";
			var (selectionStart, selectionLength) = GetSelection();
			var searchStart = next
				? (incremental ? Math.Max(0, lastSearchIndex - 1) : selectionStart + selectionLength)
				: Math.Max(0, selectionStart - 1);

			int index;

			if (next)
			{
				index = text.IndexOf(needle, searchStart, StringComparison.CurrentCulture);
				if (index == -1 && searchStart > 0)
					index = text.IndexOf(needle, 0, StringComparison.CurrentCulture);
			}
			else
			{
				index = text.LastIndexOf(needle, searchStart, StringComparison.CurrentCulture);
				if (index == -1 && text.Length > 0)
					index = text.LastIndexOf(needle, text.Length - 1, StringComparison.CurrentCulture);
			}

				if (index == -1)
				{
					SetSelection(0, 0);
					return;
				}

				lastSearchIndex = index + needle.Length;
				SetSelection(index, needle.Length);
				var end = needle.Length > 0 ? index + needle.Length - 1 : index;
				inputArea.ScrollTo(new Range<int>(index, end));
				if (!incremental)
					inputArea.Focus();
			}

		private void SelectLine()
		{
			var caret = GetSelection().start;
			var text = inputArea.Text ?? "";
			var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
			var lineEnd = text.IndexOf('\n', caret);
			if (lineEnd < 0)
				lineEnd = text.Length;
			SetSelection(lineStart, Math.Max(0, lineEnd - lineStart));
		}

		private void AdjustIndent(bool outdent)
		{
			var text = inputArea.Text ?? "";
			var (selStart, selLength) = GetSelection();

			if (text.Length == 0)
				return;

			if (selLength == 0)
			{
				var lineStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
				var lineEnd = text.IndexOf('\n', selStart);
				if (lineEnd < 0)
					lineEnd = text.Length;

				selStart = lineStart;
				selLength = lineEnd - lineStart;
			}

			var startLine = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
			var endLine = text.IndexOf('\n', selStart + selLength);
			if (endLine < 0)
				endLine = text.Length;

			var before = text.Substring(0, startLine);
			var block = text.Substring(startLine, endLine - startLine);
			var after = text.Substring(endLine);
			var lines = block.Split('\n');

			for (var i = 0; i < lines.Length; i++)
			{
				if (outdent)
				{
					if (lines[i].StartsWith("\t", StringComparison.Ordinal))
						lines[i] = lines[i].Substring(1);
					else if (lines[i].StartsWith("  ", StringComparison.Ordinal))
						lines[i] = lines[i].Substring(2);
				}
				else
				{
					lines[i] = "\t" + lines[i];
				}
			}

			var newBlock = string.Join("\n", lines);
			inputArea.Text = before + newBlock + after;
			SetSelection(startLine, newBlock.Length);
			lastKeyTime = DateTime.UtcNow;
		}

		private void TransformSelection(Func<string, string> transform)
		{
			var text = inputArea.Text ?? "";
			var (start, length) = GetSelection();

			if (length <= 0)
				return;

			var selected = text.Substring(start, length);
			var transformed = transform(selected);
			inputArea.Text = text.Substring(0, start) + transformed + text.Substring(start + length);
			SetSelection(start, transformed.Length);
			lastKeyTime = DateTime.UtcNow;
		}

		private void SetWordWrap(bool enabled)
		{
			inputArea.Wrap = enabled;
			outputArea.Wrap = enabled;
		}

		private void ZoomDefault()
		{
#if OSX
			var font = TryMonospaceFont(13);
#else
			var font = TryMonospaceFont(10);
#endif
			inputArea.Font = font;
			outputArea.Font = font;
		}

		private void ZoomIn()
		{
			inputArea.Font = new Font(inputArea.Font.Family, Math.Min(48, inputArea.Font.Size * 1.1f));
			outputArea.Font = new Font(outputArea.Font.Family, Math.Min(48, outputArea.Font.Size * 1.1f));
		}

		private void ZoomOut()
		{
			inputArea.Font = new Font(inputArea.Font.Family, Math.Max(6, inputArea.Font.Size / 1.1f));
			outputArea.Font = new Font(outputArea.Font.Family, Math.Max(6, outputArea.Font.Size / 1.1f));
		}

		private void CopyFullCode()
		{
			var text = string.IsNullOrEmpty(fullCode) ? outputArea.Text : fullCode;
			Clipboard.Instance.Text = text ?? "";
		}

		private void OpenFile()
		{
			if (!ConfirmDiscardChanges())
				return;

			var dialog = new OpenFileDialog();
			if (dialog.ShowDialog(this) == DialogResult.Ok)
			{
				LoadDataFromFile(dialog.FileName);
			}
		}

		private void LoadDataFromFile(string path)
		{
			if (!File.Exists(path))
				return;

			suppressDocumentChange = true;
			try
			{
				var fullPath = Path.GetFullPath(path);
				var text = File.ReadAllText(fullPath);
				inputArea.Text = text;
				document.LoadFile(fullPath, text);
				ResetUndoHistory();
			}
			finally
			{
				suppressDocumentChange = false;
			}

			lastKeyTime = DateTime.UtcNow;
			UpdateDocumentUi();
		}

		private void LoadScratchDocument()
		{
			suppressDocumentChange = true;
			try
			{
				inputArea.Text = File.Exists(lastrun) ? File.ReadAllText(lastrun) : "";
				document.LoadScratch();
				ResetUndoHistory();
			}
			finally
			{
				suppressDocumentChange = false;
			}

			lastKeyTime = DateTime.UtcNow;
			UpdateDocumentUi();
		}

		private bool ConfirmDiscardChanges()
		{
			if (!document.IsDirty(inputArea.Text))
				return true;

			var result = MessageBox.Show(
				this,
				$"Save changes to {document.DisplayName}?",
				"Keyview",
				MessageBoxButtons.YesNoCancel,
				MessageBoxType.Question);

			return result switch
			{
				DialogResult.Yes => SaveDocument(),
				DialogResult.No => true,
				_ => false
			};
		}

		private bool SaveDocument()
		{
			if (document.IsScratch)
				return false;

			try
			{
				File.WriteAllText(document.CurrentFilePath, inputArea.Text ?? "");
				document.MarkSaved(inputArea.Text);
				UpdateDocumentUi();
				return true;
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, $"Unable to save file: {ex.Message}", "Save Error", MessageBoxButtons.OK, MessageBoxType.Error);
				return false;
			}
		}

		private void CompileDocument()
		{
			if (!document.CanCompile || (document.IsDirty(inputArea.Text) && !SaveDocument()))
				return;

			compileScriptButton.Enabled = false;
			codeStatusLabel.Text = "Writing .cks...";

			if (KeyviewDocumentCompiler.TryCompile(document.CurrentFilePath, ch, out var outputPath, out var error))
			{
				codeStatusLabel.TextColor = Colors.Green;
				codeStatusLabel.Text = $"Wrote {outputPath}";
			}
			else
			{
				codeStatusLabel.TextColor = Colors.Red;
				codeStatusLabel.Text = "Compile failed";
				SetOutputText(error);
			}

			UpdateDocumentUi();
		}

		private void UpdateDocumentUi()
		{
			var dirty = document.IsDirty(inputArea.Text);
			Title = document.GetWindowTitle(baseTitle, dirty);
			documentStatusLabel.Text = document.GetStatusText(dirty);
			saveMenuItem.Enabled = !document.IsScratch && dirty;
			compileMenuItem.Enabled = document.CanCompile;
			compileScriptButton.Visible = !document.IsScratch;
			compileScriptButton.Enabled = document.CanCompile;
		}

#if OSX
		private void MacFileOpened(string path)
		{
			Application.Instance.AsyncInvoke(() =>
			{
				if (ConfirmDiscardChanges())
					LoadDataFromFile(path);
			});
		}
#endif

		private void SetStart()
		{
			fullCode = trimmedCode = "";
			codeStatusLabel.TextColor = Colors.Black;
			codeStatusLabel.Text = "";
		}

		private void SetSuccess(double seconds)
		{
			codeStatusLabel.TextColor = Colors.Green;
			codeStatusLabel.Text = $"Ok ({seconds:F1}s)";
		}

		private void SetFailure()
		{
			codeStatusLabel.TextColor = Colors.Red;
			codeStatusLabel.Text = "Error";
			SetOutputText("");
		}

		private void SetOutputText(string text)
		{
			outputArea.Text = text;
		}

		private void UpdateOutputFromCache()
		{
			var desired = fullCodeCheck.Checked == true ? fullCode : trimmedCode;
			if (string.IsNullOrEmpty(desired))
				return;
			SetOutputText(desired);
		}

		private async void Timer_Elapsed(object sender, EventArgs e)
		{
			if (!isCompiling && (force || ((DateTime.UtcNow - lastKeyTime).TotalSeconds >= updateFreqSeconds && lastKeyTime > lastCompileTime)) && !string.IsNullOrEmpty(inputArea.Text))
			{
				timer.Stop();
				isCompiling = true;
				lastCompileTime = DateTime.UtcNow;

				try
				{
					await KeyviewCompilerRunner.RunCompile(
						inputArea.Text,
						ch,
						code => fullCode = code,
						code => trimmedCode = code,
						trimend,
						trimstr,
						SetStart,
						SetSuccess,
						SetFailure,
						text => codeStatusLabel.Text = text,
						null,
						SetOutputText,
						() => fullCodeCheck.Checked == true,
						() => runScriptButton.Enabled = false,
						() => runScriptButton.Enabled = true,
						AutosaveScratchDocument,
						null,
						null);
				}
				finally
				{
					isCompiling = false;
					timer.Start();
				}
			}

			if (force)
				force = false;
		}

		private void RunStopScript()
		{
			if (scriptProcess != null)
			{
				scriptProcess.Kill();
				scriptProcess = null;
			}

			if (runScriptButton.Text == runScriptText["Stop"])
				return;

			if (CompilerHelper.compiledasm == null)
			{
				MessageBox.Show(this, "Please wait, code is still compiling...", "Error", MessageBoxButtons.OK, MessageBoxType.Error);
				return;
			}

			var keysharpExe = GetKeysharpExecutable();

			if (!File.Exists(keysharpExe))
			{
				MessageBox.Show(this, $"Keysharp executable not found:\n{keysharpExe}\n\nCopy Keysharp.app to /Applications/ or run Keyview from the same folder as Keysharp.", "Launch Error", MessageBoxButtons.OK, MessageBoxType.Error);
				return;
			}

			scriptProcess = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = keysharpExe,
					Arguments = "--assembly *",
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			scriptProcess.EnableRaisingEvents = true;
			scriptProcess.ErrorDataReceived += (_, e) =>
			{
				if (!string.IsNullOrEmpty(e.Data))
					Application.Instance.AsyncInvoke(() => outputArea.Append($"{e.Data}\n", true));
			};
			scriptProcess.Exited += (_, _) =>
			{
				Application.Instance.AsyncInvoke(() =>
				{
					runScriptButton.Text = runScriptText["Run"];
					scriptProcess = null;
				});
			};
			_ = scriptProcess.Start();
			scriptProcess.BeginErrorReadLine();

			// Write the raw assembly bytes and close stdin; the child ("--assembly *") reads to EOF.
			try
			{
				using var stdin = scriptProcess.StandardInput.BaseStream;
				stdin.Write(CompilerHelper.compiledBytes, 0, CompilerHelper.compiledBytes.Length);
				stdin.Flush();
			}
			catch (IOException)
			{
				// Keysharp exited before reading stdin — error output will appear via stderr above.
				runScriptButton.Text = runScriptText["Run"];
				scriptProcess = null;
				return;
			}

			runScriptButton.Text = runScriptText["Stop"];
		}

		private static string GetKeysharpExecutable()
		{
#if OSX && !DEBUG
			// Prefer the binary installed to /Applications/ (pkg install).
			// Check the binary itself rather than the /usr/local/bin shim, which may be stale.
			const string installed = "/Applications/Keysharp.app/Contents/MacOS/Keysharp";
			if (File.Exists(installed))
				return installed;
#endif
			// Fallback: sibling binary (DMG run, ~/Applications/, or debug build).
			return Path.Combine(AppContext.BaseDirectory ?? Path.GetDirectoryName(Environment.ProcessPath), "Keysharp");
		}

		private void AutosaveScratchDocument()
		{
			if (!document.IsScratch)
				return;

			var dir = Path.GetDirectoryName(lastrun);
			try
			{
				if (!Directory.Exists(dir))
					_ = Directory.CreateDirectory(dir);

				File.WriteAllText(lastrun, inputArea.Text ?? "");
			}
			catch (Exception ex)
			{
				documentStatusLabel.Text = $"Scratch autosave failed: {ex.Message}";
			}
		}
	}
#endif

	internal static class KeyviewCompilerRunner
	{
		internal static async Task RunCompile(
			string inputText,
			CompilerHelper compiler,
			Action<string> setFullCode,
			Action<string> setTrimmedCode,
			char[] trimend,
			string trimstr,
			Action setStart,
			Action<double> setSuccess,
			Action setFailure,
			Action<string> setStatus,
			Action refreshStatus,
			Action<string> setOutput,
			Func<bool> useFullCode,
			Action disableRunButton,
			Action enableRunButton,
			Action writeLastRun,
			Action beforeOutput,
			Action afterOutput)
		{
			Script script = null;
			var startTime = DateTime.UtcNow;
			try
			{
				script = new Script();
				script.SuppressErrorOccurredDialog = true;
				CompilerHelper.compiledasm = null;
				disableRunButton?.Invoke();
				setStart?.Invoke();
				setStatus?.Invoke("Creating DOM from script...");
				refreshStatus?.Invoke();
				var (unit, domerrs) = await Task.Run(() => compiler.CreateCompilationUnitFromFile(inputText)).ConfigureAwait(true);

				if (domerrs.HasErrors)
				{
					var (errors, warnings) = CompilerHelper.GetCompilerErrors(domerrs);
					setFailure?.Invoke();
					var txt = "Error creating DOM from script.";
					if (errors.Length > 0)
						txt += $"\n\n{errors}";
					if (warnings.Length > 0)
						txt += $"\n\n{warnings}";
					setOutput?.Invoke(txt);
					return;
				}

				setStatus?.Invoke("Creating C# code from DOM...");
				refreshStatus?.Invoke();
				var code = await Task.Run(() => PrettyPrinter.Print(unit)).ConfigureAwait(true);

#if DEBUG
				var normalized = unit.NormalizeWhitespace("\t", Environment.NewLine).ToString();
				if (code != normalized)
					throw new Exception("Code formatting mismatch");
#endif

				setStatus?.Invoke("Compiling C# code...");
				refreshStatus?.Invoke();
				var (results, ms, compileexc) = await Task.Run(() => compiler.Compile(unit, "Keyview", Path.GetFullPath(Path.GetDirectoryName(Environment.ProcessPath)))).ConfigureAwait(true);

				if (results == null)
				{
					setFailure?.Invoke();
					setOutput?.Invoke($"Error compiling C# code to executable: {(compileexc != null ? compileexc.Message : string.Empty)}\n\n{code}");
				}
				else if (results.Success)
				{
					setSuccess?.Invoke((DateTime.UtcNow - startTime).TotalSeconds);
					setFullCode(code);
					var token = "[System.STAThreadAttribute()]";
					var start = code.IndexOf(token);
					code = code.AsSpan(start + token.Length + 2).TrimEnd(trimend).ToString();
					var sb = new StringBuilder(code.Length);

					foreach (var line in code.SplitLines())
						_ = sb.AppendLine(line.TrimNofAnyFromStart(trimstr, 2));

					var trimmedCode = sb.ToString().TrimEnd(trimend);
					setTrimmedCode(trimmedCode);
					beforeOutput?.Invoke();
					setOutput?.Invoke(useFullCode() ? code : trimmedCode);
					afterOutput?.Invoke();
					writeLastRun?.Invoke();
					_ = ms.Seek(0, SeekOrigin.Begin);
					var arr = ms.ToArray();
					CompilerHelper.compiledBytes = arr;
					CompilerHelper.compiledasm = Assembly.Load(arr);
				}
				else
				{
					setFailure?.Invoke();
					setOutput?.Invoke(CompilerHelper.HandleCompilerErrors(results.Diagnostics, "Keyview", "Compiling C# code to executable", compileexc != null ? compileexc.Message : string.Empty) + "\n" + code);
				}

				ms?.Dispose();
			}
			catch (Exception ex)
			{
				setFailure?.Invoke();
				setOutput?.Invoke(ex.ToString());
			}
			finally
			{
				script?.Dispose();
				enableRunButton?.Invoke();
			}
		}
	}

	internal sealed class KeyviewDocumentState
	{
		private string savedText = "";

		internal string CurrentFilePath { get; private set; }
		internal bool IsScratch => string.IsNullOrEmpty(CurrentFilePath);
		internal bool CanCompile => !IsScratch && IsSourceFile(CurrentFilePath);
		internal string DisplayName => IsScratch ? "Scratch document" : Path.GetFileName(CurrentFilePath);

		internal void LoadFile(string path, string text)
		{
			CurrentFilePath = Path.GetFullPath(path);
			savedText = text ?? "";
		}

		internal void LoadScratch()
		{
			CurrentFilePath = null;
			savedText = "";
		}

		internal void MarkSaved(string text) => savedText = text ?? "";

		internal bool IsDirty(string text) => !IsScratch && !string.Equals(savedText, text ?? "", StringComparison.Ordinal);

		internal string GetWindowTitle(string baseTitle, bool dirty)
		{
			if (IsScratch)
				return $"{baseTitle} — Scratchpad (autosaved)";

			return $"{DisplayName}{(dirty ? " *" : "")} — {baseTitle}";
		}

		internal string GetStatusText(bool dirty)
		{
			if (IsScratch)
				return "Scratchpad document — autosaved";

			return $"{CurrentFilePath}{(dirty ? " — Modified" : "")}";
		}

		private static bool IsSourceFile(string path)
		{
			var extension = Path.GetExtension(path);
			return extension.Equals(".ahk", StringComparison.OrdinalIgnoreCase)
				   || extension.Equals(".ks", StringComparison.OrdinalIgnoreCase);
		}
	}

	internal static class KeyviewDocumentCompiler
	{
		internal static bool TryCompile(string sourcePath, CompilerHelper compiler, out string outputPath, out string error)
		{
			outputPath = Path.ChangeExtension(sourcePath, ".cks");
			error = null;

			// Constructing a Script overwrites the global Script.TheScript singleton, so save and
			// restore it around the temporary compile-only script to avoid clobbering whatever
			// script Keyview itself may already be hosting (e.g. one started via "Run script").
			var previousScript = Script.TheScript;

			try
			{
				using var script = new Script();
				script.SuppressErrorOccurredDialog = true;
				var sourceDirectory = Path.GetDirectoryName(sourcePath);
				var nameNoExt = Path.GetFileNameWithoutExtension(sourcePath);
				var (bytes, result) = compiler.CompileCodeToByteArray(sourcePath, nameNoExt, sourceDirectory);

				if (bytes == null)
				{
					error = result;
					return false;
				}

				File.WriteAllBytes(outputPath, bytes);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.ToString();
				return false;
			}
			finally
			{
				Script.TheScript = previousScript;
			}
		}
	}
}
