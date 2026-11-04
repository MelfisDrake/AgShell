using AgShell.Protocol;

namespace AgShell.App
{
    internal sealed class WindowCloseResponse
    {
        private ControlRequest? closing;
        private ControlRequest? pending;
        private Action? shutdown;

        public void Close(ControlRequest request, Action close)
        {
            closing = request;
            try
            {
                close();
            }
            finally
            {
                closing = null;
            }
        }

        public bool DeferShutdown(Action action)
        {
            if (closing is null)
            {
                return false;
            }

            pending = closing;
            shutdown = action;
            return true;
        }

        public void Complete(ControlRequest request)
        {
            if (!ReferenceEquals(pending, request))
            {
                return;
            }

            Action? action = shutdown;
            pending = null;
            shutdown = null;
            action?.Invoke();
        }
    }
}
