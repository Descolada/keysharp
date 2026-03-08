#if OSX
namespace Keysharp.Core.Common.Window
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

		internal MessageFilter(Script associatedScript)
		{
			script = associatedScript;
		}

		internal void Attach()
		{
			// No global event filter is currently attached on macOS.
			// Message callbacks can still be invoked through explicit call sites.
		}

		internal void Detach()
		{
		}

		internal bool CallEventHandlers(ref Message m, bool buffered = false)
		{
			if (script.GuiData.onMessageHandlers.TryGetValue(m.Msg, out var monitor))
			{
				object eventInfo = 0L;
				long hwnd = m.HWnd;

				if (MessagesEqual(handledMsg, m))
					eventInfo = A_TickCount;

				object[] args = [m.WParam.ToInt64(), m.LParam.ToInt64(), (long)m.Msg, m.HWnd.ToInt64()];

				if (buffered)
				{
					foreach (var registration in monitor.GetRegistrationsSnapshot())
					{
						script.EventScheduler.Enqueue(new ScriptEvent(
							ScriptEventKind.MessageCallback,
							ScriptEventQueue.Normal,
							0,
							() => registration.TryExecuteBuffered(script, args, eventInfo, hwnd, out _)));
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
	}
}
#endif
