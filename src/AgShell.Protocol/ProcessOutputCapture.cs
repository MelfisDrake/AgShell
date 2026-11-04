using System.Diagnostics;

namespace AgShell.Protocol
{
    /// <summary>What a helper process printed and how it ended. <paramref name="ExitCode"/> is null when it did not exit.</summary>
    public sealed record ProcessOutput(string Stdout, string Stderr, int? ExitCode, bool TimedOut)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// agterm's ProcessOutputCapture: stdout and stderr of a helper process drained together while it runs, so a full pipe
    /// cannot hold up its exit; stdin is closed at once. Past the timeout the process tree is killed (Windows has no SIGTERM
    /// for a console program to answer first), and both streams get <see cref="TerminationGrace"/> after the exit to reach
    /// their end. A stream a descendant still holds open past that loses the output: the result is null, as in agterm.
    /// A program that does not start throws as <see cref="Process.Start()"/> does.
    /// </summary>
    public static class ProcessOutputCapture
    {
        public static readonly TimeSpan TerminationGrace = TimeSpan.FromMilliseconds(250);

        public static ProcessOutput? Run(ProcessStartInfo start, TimeSpan? timeout)
        {
            using Process process = Launch(start);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool timedOut = !process.WaitForExit(timeout is TimeSpan limit ? (int)limit.TotalMilliseconds : Timeout.Infinite);
            if (timedOut)
            {
                Terminate(process);
                process.WaitForExit((int)TerminationGrace.TotalMilliseconds);
            }

            Task streams = Task.WhenAll(stdout, stderr);
            bool drained = Task.WhenAny(streams, Task.Delay(TerminationGrace)).GetAwaiter().GetResult() == streams;
            return Collect(process, stdout, stderr, drained, timedOut);
        }

        public static async Task<ProcessOutput?> RunAsync(ProcessStartInfo start, TimeSpan? timeout)
        {
            using Process process = Launch(start);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task exited = process.WaitForExitAsync();
            bool timedOut = timeout is TimeSpan limit && await Task.WhenAny(exited, Task.Delay(limit)).ConfigureAwait(false) != exited;
            if (timedOut)
            {
                Terminate(process);
                await Task.WhenAny(exited, Task.Delay(TerminationGrace)).ConfigureAwait(false);
            }
            else
            {
                await exited.ConfigureAwait(false);
            }

            Task streams = Task.WhenAll(stdout, stderr);
            bool drained = await Task.WhenAny(streams, Task.Delay(TerminationGrace)).ConfigureAwait(false) == streams;
            return Collect(process, stdout, stderr, drained, timedOut);
        }

        private static Process Launch(ProcessStartInfo start)
        {
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");
            process.StandardInput.Close();
            return process;
        }

        private static void Terminate(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
            }
        }

        private static ProcessOutput? Collect(Process process, Task<string> stdout, Task<string> stderr, bool drained, bool timedOut)
        {
            if (!drained || stdout.IsFaulted || stderr.IsFaulted)
            {
                // The reads end with the handles the disposal closes; their faults are nobody's business.
                _ = stdout.ContinueWith(task => task.Exception, TaskScheduler.Default);
                _ = stderr.ContinueWith(task => task.Exception, TaskScheduler.Default);
                return null;
            }

            int? exitCode = process.HasExited ? process.ExitCode : null;
            return new ProcessOutput(stdout.Result, stderr.Result, exitCode, timedOut);
        }
    }

    /// <summary>
    /// The last bytes a stream carries, kept within a fixed bound however much is written: agterm's CommandFailure tail.
    /// The stream is drained to its end in the background; <see cref="Snapshot"/> takes what is there so far.
    /// </summary>
    public sealed class StreamTail
    {
        private readonly byte[] ring;
        private readonly object gate = new object();
        private long written;

        public StreamTail(Stream stream, int limit)
        {
            ring = new byte[limit];
            Completion = DrainAsync(stream);
        }

        /// <summary>Done at the stream's end, which a descendant holding the stream may put off indefinitely.</summary>
        public Task Completion { get; }

        /// <summary>The tail once the stream ends or <paramref name="grace"/> passes, whichever is first.</summary>
        public async Task<byte[]> TakeAsync(TimeSpan grace)
        {
            await Task.WhenAny(Completion, Task.Delay(grace)).ConfigureAwait(false);
            return Snapshot();
        }

        public byte[] Snapshot()
        {
            if (ring.Length == 0)
            {
                return Array.Empty<byte>();
            }

            lock (gate)
            {
                int length = (int)Math.Min(written, ring.Length);
                byte[] tail = new byte[length];
                int start = (int)((written - length) % ring.Length);
                int first = Math.Min(length, ring.Length - start);
                Array.Copy(ring, start, tail, 0, first);
                Array.Copy(ring, 0, tail, first, length - first);
                return tail;
            }
        }

        private async Task DrainAsync(Stream stream)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    Append(buffer.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
            }
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            if (ring.Length == 0)
            {
                return;
            }

            lock (gate)
            {
                if (data.Length > ring.Length)
                {
                    written += data.Length - ring.Length;
                    data = data[^ring.Length..];
                }

                foreach (byte value in data)
                {
                    ring[(int)(written % ring.Length)] = value;
                    written++;
                }
            }
        }
    }
}
