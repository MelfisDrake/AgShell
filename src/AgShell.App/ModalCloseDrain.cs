using AgShell.Protocol;

namespace AgShell.App
{
    internal sealed class ModalCloseDrain
    {
        public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

        private readonly HashSet<(string Family, string Id)> pending = new HashSet<(string, string)>();
        private Action? finish;

        public void Track(string family, string id) => pending.Add((family, id));

        public bool Begin(Action completed)
        {
            if (pending.Count == 0)
            {
                completed();
                return false;
            }

            finish = completed;
            return true;
        }

        public void Written(ControlRequest request, ControlResponse response)
        {
            string? family = request.Cmd switch { "ask.result" => "ask", "pick.result" => "pick", _ => null };
            string? outcome = family == "ask" ? response.Result?.Ask?.Result : response.Result?.Pick?.Result;
            if (!response.Ok || family is null || request.Target is not string id || outcome != "cancelled")
            {
                return;
            }

            if (pending.Remove((family, id)) && pending.Count == 0)
            {
                Expire();
            }
        }

        public void Expire()
        {
            Action? completed = finish;
            finish = null;
            pending.Clear();
            completed?.Invoke();
        }
    }
}
