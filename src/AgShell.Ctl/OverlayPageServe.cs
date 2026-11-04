using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// <c>agshellctl session overlay page-serve &lt;token&gt;</c>: agterm's page-serve helper, run through ssh by the machine presenting a
    /// session. It claims the page with its single-use token over the control pipe, and on an ok answer relays that
    /// connection to stdin and stdout until either side ends: the app here carries the page's channel.
    /// </summary>
    internal static class OverlayPageServe
    {
        public static int Run(string token, string? pipeOverride)
        {
            string pipe = ControlEndpoint.ResolvePipe(pipeOverride);
            ControlRequest request = new ControlRequest { Cmd = ControlEndpoint.PageServeCommand, Target = token };
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

            if (opened.connection is not Stream channel)
            {
                Console.Error.WriteLine("error: " + (opened.response.Error ?? "the app refused the stream"));
                return 1;
            }

            using (channel)
            {
                using Stream input = Console.OpenStandardInput();
                using Stream output = Console.OpenStandardOutput();
                Task inbound = input.CopyToAsync(channel);
                Task outbound = channel.CopyToAsync(output);
                Task.WaitAny(inbound, outbound);
            }

            return 0;
        }
    }
}
