using Keysharp.Builtins;
#if LINUX
using Gdk;

namespace Keysharp.Internals.Window.Linux
{
	internal struct Message
	{
		public nint HWnd;
		public int Msg;
		public nint WParam;
		public nint LParam;
		public nint Result;
	}

	internal class MessageFilter
	{
		private readonly Script script;
		internal Message handledMsg;
		private FilterFunc gtkFilter;
		private bool filterAttached;

		internal MessageFilter(Script associatedScript)
		{
			script = associatedScript;
		}

		internal void Attach()
		{
			if (filterAttached)
				return;

			gtkFilter = GtkFilter;
			var root = Gdk.Screen.Default?.RootWindow;
			if (root != null)
			{
				try
				{
					root?.AddFilter(gtkFilter);
					filterAttached = true;
				}
				catch
				{
					OutputDebugLine("Failed to attach GTK message filter");
				}
			}
		}

		internal void Detach()
		{
			if (!filterAttached || gtkFilter == null)
				return;

			var root = Gdk.Screen.Default?.RootWindow;
			root?.RemoveFilter(gtkFilter);
			gtkFilter = null;
			filterAttached = false;
		}

		internal bool CallEventHandlers(ref Message m, bool buffered = false)
		{
			if (script.GuiData.onMessageHandlers.TryGetValue(m.Msg, out var monitor))
			{
				object eventInfo = 0L;
				long hwnd = m.HWnd;

				if (MessagesEqual(handledMsg, m))
				{
					eventInfo = A_TickCount;
				}

				object[] args = [m.WParam.ToInt64(), m.LParam.ToInt64(), (long)m.Msg, m.HWnd.ToInt64()];

				if (buffered)
				{
					foreach (var registration in monitor.GetRegistrationsSnapshot())
					{
							registration.OwnerScheduler.Enqueue(ScriptEventQueue.Normal, () => registration.TryExecuteBuffered(script, args, eventInfo, hwnd, out _));
					}
				}
				else if (monitor.TryExecuteEmergency(script, args, eventInfo, hwnd, out var result))
				{
					m.Result = (nint)result;

					if (m.Result != 0)
						return true;
				}
			}

			return false;
		}

		private static bool MessagesEqual(Message left, Message right)
			=> left.HWnd == right.HWnd
				&& left.Msg == right.Msg
				&& left.WParam == right.WParam
				&& left.LParam == right.LParam
				&& left.Result == right.Result;

		private FilterReturn GtkFilter(IntPtr xevent, Event evnt)
		{
			// No Win32 message on GTK; use the GDK event type as the message id.
			var msg = new Message
			{
				HWnd = evnt.Window.Handle,
				Msg = (int)evnt.Type,
				WParam = 0,
				LParam = 0,
				Result = 0,
			};

			handledMsg = msg;
			_ = CallEventHandlers(ref msg, true);
			return FilterReturn.Continue;
		}
	}
}
#endif
