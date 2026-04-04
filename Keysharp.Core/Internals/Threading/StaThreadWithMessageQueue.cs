using Keysharp.Builtins;
#if WINDOWS
namespace Keysharp.Internals.Threading
{
	/// <summary>
	/// Creates an STA thread which also has a message queue.
	/// This allows things like a mouse/keyboard hook to be run
	/// on a thread other than the main one.
	/// Gotten from: https://stackoverflow.com/questions/21680738/how-to-post-messages-to-an-sta-thread-running-a-message-pump
	/// </summary>
	internal class StaThreadWithMessageQueue : IDisposable
	{
		private readonly Thread thread;
		private SynchronizationContext ctx;

		public StaThreadWithMessageQueue()
		{
			using var initialized = new ManualResetEventSlim(false);
			System.Runtime.ExceptionServices.ExceptionDispatchInfo initializationError = null;
			thread = new Thread(() =>
			{
				try
				{
					Application.Idle += Initialize;
					Application.Run();
				}
				catch (Exception ex)
				{
					initializationError = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
					initialized.Set();
				}
			})
			{
				IsBackground = true
			};
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			initialized.Wait();
			initializationError?.Throw();

			void Initialize(object sender, EventArgs e)
			{
				Application.Idle -= Initialize;
				ctx = SynchronizationContext.Current;
				initialized.Set();
			}
		}

		public void Post(SendOrPostCallback d, object state)
		{
			if (ctx == null) throw new ObjectDisposedException("StaThreadWithMessageQueue");

			ctx.Post(d, state);
		}

		public void Send(SendOrPostCallback d, object state)
		{
			if (ctx == null) throw new ObjectDisposedException("StaThreadWithMessageQueue");

			ctx.Send(d, state);
		}

		public void Dispose()
		{
			if (ctx != null)
			{
				ctx.Send((_) => Application.ExitThread(), null);
				ctx = null;
			}

			if (!ReferenceEquals(Thread.CurrentThread, thread))
				thread.Join();
		}

		public bool IsDisposed() => ctx == null;
	}
}
#endif
