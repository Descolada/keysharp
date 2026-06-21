using Keysharp.Builtins;
#if !WINDOWS
namespace Keysharp.Internals.Window.Unix
{
	public class MenuStrip : Panel
	{
		private readonly MenuStripToolStrip toolStrip;

		internal Eto.Forms.MenuBar EtoMenuBar { get; } = new Eto.Forms.MenuBar();
		public ToolStripItemCollection Items { get; }
		public DockStyle Dock { get; set; } = DockStyle.Top;
		public ToolStrip ToolStrip => toolStrip;

		public MenuStrip()
		{
			toolStrip = new MenuStripToolStrip(this);
			Items = toolStrip.Items;
		}

		private bool systemMenuLoaded;

		// Called once the menu bar has been assigned to a window, after Eto's one-time CreateSystemMenu
		// (via MenuBar.OnPreLoad) has merged the standard App/Edit/Window menus. After that, OnPreLoad
		// won't run again, so SyncEtoMenuBar has to re-merge them itself.
		internal void MarkSystemMenuLoaded() => systemMenuLoaded = true;

		internal void SyncEtoMenuBar()
		{
			EtoMenuBar.Items.Clear();
			foreach (var item in Items)
			{
				item.ResetEtoItemRecursive();
				EtoMenuBar.Items.Add(item.BuildEtoItem());
			}

			// The Items.Clear() above also drops the standard App/Edit/Window menus Eto merged in on first
			// load, which would silently remove the platform editing shortcuts (Cmd+C/A/... on macOS) every
			// time the script changes its menu. Re-merge them. CreateSystemMenu honors
			// MenuBar.IncludeSystemItems, so setting that to None opts out. Skipped before the first load,
			// where MenuBar.OnPreLoad performs the initial merge (calling it here too would double entries).
			if (systemMenuLoaded && EtoMenuBar.Handler is Eto.Forms.MenuBar.IHandler handler)
				handler.CreateSystemMenu();
		}

		private sealed class MenuStripToolStrip : ToolStrip
		{
			private readonly MenuStrip owner;

			internal MenuStripToolStrip(MenuStrip owner)
			{
				this.owner = owner;
			}

			internal override void SyncEtoItems()
			{
				owner.SyncEtoMenuBar();
			}
		}
	}
}
#endif

