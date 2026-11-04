using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// <c>agshellctl live present &lt;session&gt;</c>: agterm's zmx present, run over ssh by agshell on another machine that
    /// attached the session. It opens the session's presentation stream over the control pipe and relays it: the app pushes
    /// JSON lines to stdout as the session changes, and the answers and the hello read from stdin go back to it. Either side
    /// closing ends it, and the presenter role goes back at once.
    /// </summary>
    internal static class LivePresent
    {
        public static int Run(string session, string? pipeOverride)
        {
            string pipe = ControlEndpoint.ResolvePipe(pipeOverride);
            ControlRequest request = new ControlRequest { Cmd = ControlEndpoint.PresentCommand, Target = session };
            (ControlResponse response, Stream? connection) opened;
            try
            {
                opened = ControlClient.OpenStreamAsync(pipe, request, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (ControlClientException ex)
            {
                Console.Error.WriteLine((ex.Connect ? "Error: " : "error: ") + ex.Message);
                return 1;
            }

            if (opened.connection is not Stream stream)
            {
                Console.Error.WriteLine("error: " + (opened.response.Error ?? "the app refused the stream"));
                return 1;
            }

            using (stream)
            {
                using Stream input = Console.OpenStandardInput();
                using Stream output = Console.OpenStandardOutput();
                Task inbound = input.CopyToAsync(stream);
                Task outbound = stream.CopyToAsync(output);
                Task.WaitAny(inbound, outbound);
            }

            return 0;
        }
    }
}
