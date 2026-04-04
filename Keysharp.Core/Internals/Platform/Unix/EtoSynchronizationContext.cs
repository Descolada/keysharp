using Keysharp.Builtins;
#if !WINDOWS
namespace Keysharp.Internals.Platform.Unix
{
    internal sealed class EtoSynchronizationContext(Application application) : SynchronizationContext
    {
        private readonly Application app = application;

        public override SynchronizationContext CreateCopy() => this;

        public override void Post(SendOrPostCallback d, object state) => app.AsyncInvoke(() => d(state));

        public override void Send(SendOrPostCallback d, object state)
        {
            if (app.IsUIThread)
                d(state);
            else
                app.Invoke(() => d(state));
        }
    }
}
#endif
