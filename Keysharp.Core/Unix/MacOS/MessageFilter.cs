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
		}

		internal void Detach()
		{
		}

		internal bool CallEventHandlers(ref Message m)
		{
			if (!script.GuiData.onMessageHandlers.TryGetValue(m.Msg, out var monitor))
				return false;

			var ptv = script.Threads.CurrentThread;

			if (!script.Threads.AnyThreadsAvailable() || ptv.priority > 0)
				return false;

			if (monitor.instanceCount >= monitor.maxInstances)
				return false;

			monitor.instanceCount++;
			object res = null;
			object eventInfo = 0L;
			long hwnd = m.HWnd;

			if (MessagesEqual(handledMsg, m))
				eventInfo = A_TickCount;

			try
			{
				var handlers = monitor.funcs;
				object[] args = [m.WParam.ToInt64(), m.LParam.ToInt64(), (long)m.Msg, m.HWnd.ToInt64()];
				if (handlers.Any())
				{
					var currentScript = Script.TheScript;

					foreach (var handler in handlers)
					{
						if (handler == null)
							continue;

						var (pushed, tv) = currentScript.Threads.BeginThread();
						if (!pushed)
							continue;

						tv.eventInfo = eventInfo;
						tv.hwndLastUsed = hwnd;
						_ = Flow.TryCatch(() =>
						{
							res = handler.Call(args);
							_ = currentScript.Threads.EndThread((pushed, tv));
						}, true, (pushed, tv));

						if (Script.ForceLong(res) != 0L)
							break;
					}

					currentScript.ExitIfNotPersistent();
				}
			}
			finally
			{
				monitor.instanceCount--;
			}

			m.Result = (nint)Script.ForceLong(res);
			return m.Result != 0;
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
