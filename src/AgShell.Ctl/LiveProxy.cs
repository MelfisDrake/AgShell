using System.IO.Pipes;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// <c>agshellctl live proxy PANE</c>: a live pane's daemon pipe on stdin and stdout, so agshell on another machine reaches
    /// this one's kept shell through ssh, one connection per pane, as agterm's remote attach runs zmx attach for each. Bytes
    /// pass as they are; the daemon speaks the protocol. Exits when either side closes.
    /// </summary>
    internal static class LiveProxy
    {
        public static int Run(string pane)
        {
            if (!Guid.TryParse(pane, out _))
            {
                Console.Error.WriteLine("error: no live pane " + pane);
                return 1;
            }

            string stateDirectory = ControlEndpoint.StateDirectory();
            string name = ControlEndpoint.LivePanePipe(stateDirectory, pane);
            LiveLog log = LiveLog.ForClients(stateDirectory);
            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(2000);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException)
            {
                log.Info("connect failed (" + ex.GetType().Name + ") session=" + name);
                Console.Error.WriteLine("error: no live pane " + pane + " here (restore mode live, and AgShell started with it)");
                return 1;
            }

            try
            {
                PipeOwner.Verify(pipe);
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn("pipe owner refused session=" + name + " err=" + ex.Message);
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }

            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            Task up = input.CopyToAsync(pipe);
            Task down = CopyFlushingAsync(pipe, output);
            log.Info("client loop session=" + name);
            log.Info(Task.WaitAny(up, down) == 0 ? "eof stdin session=" + name : "server closed connection session=" + name);
            return 0;
        }

        /// <summary>Copies and flushes each read at once: a keystroke's echo must not wait in a buffer.</summary>
        private static async Task CopyFlushingAsync(Stream from, Stream to)
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                await to.FlushAsync();
            }
        }
    }
}
