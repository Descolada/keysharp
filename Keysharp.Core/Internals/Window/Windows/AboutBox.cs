using Keysharp.Builtins;
namespace Keysharp.Internals.Window.Windows
{
	partial class AboutBox : Form
	{
		public AboutBox()
		{
			InitializeComponent();
			Text = AboutInfo.Title;
			labelProductName.Text = AboutInfo.ProductName;
			linkLabel.Text = AboutInfo.Url;
			textBoxDescription.Text = AboutInfo.Description.ReplaceLineEndings(Environment.NewLine);
		}

		public string AssemblyVersion => AboutInfo.Version;

		private void okButton_Click(object sender, EventArgs e) => Close();

		private void linkLabel_LinkClicked(object sender, System.Windows.Forms.LinkLabelLinkClickedEventArgs e) => _ = Process.Start(new ProcessStartInfo(linkLabel.Text) { UseShellExecute = true });
	}
}
