namespace AgShell.Protocol.Tests
{
    public class ControlServerTests
    {
        [Fact]
        public async Task DisconnectedCloseClientCompletesReplyWithoutReportingItWritten()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<ControlRequest> completed = new TaskCompletionSource<ControlRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool written = false;
            await using ControlServer server = new ControlServer(pipe, async request =>
            {
                entered.SetResult();
                await release.Task;
                return ControlResponse.Success(new ControlResult { Text = new string('x', 128 * 1024) });
            });
            server.Written += (request, response) => written = true;
            server.ReplyCompleted += (request, response) => completed.SetResult(request);
            Assert.True(server.Start());
            using (System.IO.Pipes.NamedPipeClientStream client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);
                await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"window.close\"}\n"));
                await client.FlushAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            release.SetResult();
            ControlRequest request = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("window.close", request.Cmd);
            Assert.False(written);
        }

        [Fact]
        public async Task RequestAndResponseRoundTripOverThePipe()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            ControlRequest? received = null;
            await using ControlServer server = new ControlServer(pipe, request =>
            {
                received = request;
                return Task.FromResult(ControlResponse.Success(new ControlResult { Text = "привет", Id = "x" }));
            });
            Assert.True(server.Start());

            ControlReply reply = await ControlClient.SendAsync(pipe, new ControlRequest
            {
                Cmd = "session.type",
                Target = "9f3c",
                Args = new ControlArgs { Text = "ls\n", Targets = new List<string> { "a", "b" } },
            }, TimeSpan.FromSeconds(5));

            Assert.True(reply.Response.Ok);
            Assert.Equal("привет", reply.Response.Result!.Text);
            Assert.Contains("привет", reply.Line);
            Assert.DoesNotContain("tree", reply.Line);
            Assert.Equal("session.type", received!.Cmd);
            Assert.Equal("9f3c", received.Target);
            Assert.Equal("ls\n", received.Args!.Text);
            Assert.Equal(new[] { "a", "b" }, received.Args.Targets);
        }

        [Fact]
        public async Task WrittenFollowsTheWholeReplyWithItsRequest()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            TaskCompletionSource<(ControlRequest, ControlResponse)> written = new TaskCompletionSource<(ControlRequest, ControlResponse)>();
            await using ControlServer server = new ControlServer(pipe, request => Task.FromResult(ControlResponse.Success(new ControlResult { Text = "ok" })));
            server.Written += (request, response) => written.TrySetResult((request, response));
            Assert.True(server.Start());

            ControlReply reply = await ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "live.reset" }, TimeSpan.FromSeconds(5));
            (ControlRequest request, ControlResponse response) = await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(reply.Response.Ok);
            Assert.Equal("live.reset", request.Cmd);
            Assert.Equal("ok", response.Result!.Text);
        }

        [Fact]
        public async Task ManyClientsAreAllServed()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            await using ControlServer server = new ControlServer(pipe, async request =>
            {
                await Task.Delay(50);
                return ControlResponse.Success(new ControlResult { Text = request.Cmd });
            });
            server.Start();

            ControlReply[] replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "c" + index }, TimeSpan.FromSeconds(5))));

            Assert.Equal(Enumerable.Range(0, 8).Select(index => "c" + index), replies.Select(reply => reply.Response.Result!.Text));
        }

        [Fact]
        public async Task RequestsAreHandledOneAtATimeButNetworkWaitsStepAside()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            int running = 0;
            int most = 0;
            TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ControlServer server = new ControlServer(pipe, async request =>
            {
                if (request.Cmd == "live.tree")
                {
                    await release.Task;
                    return ControlResponse.Success();
                }

                most = Math.Max(most, Interlocked.Increment(ref running));
                await Task.Delay(30);
                Interlocked.Decrement(ref running);
                return ControlResponse.Success();
            });
            server.Start();

            // As agterm's waitsOnNetwork: a live tree waiting on ssh leaves the line, and the rest still go one by one.
            Task<ControlReply> waiting = ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "live.tree" }, TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            await Task.WhenAll(Enumerable.Range(0, 6).Select(index => ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "c" + index }, TimeSpan.FromSeconds(10))));
            Assert.Equal(1, most);
            Assert.False(waiting.IsCompleted);
            release.SetResult();
            Assert.True((await waiting).Response.Ok);
        }

        [Fact]
        public async Task ALastLineWithoutNewlineCountsAndASilentClientIsAnswered()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            TaskCompletionSource<string> handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ControlServer server = new ControlServer(pipe, request =>
            {
                handled.TrySetResult(request.Cmd);
                return Task.FromResult(ControlResponse.Success());
            });
            server.Start();

            // As agterm's readLine: the end of the stream after an unterminated line still delivers that line.
            using (System.IO.Pipes.NamedPipeClientStream client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut))
            {
                await client.ConnectAsync(2000);
                await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"tree\"}"));
                await client.FlushAsync();
                if (OperatingSystem.IsWindows())
                {
                    client.WaitForPipeDrain();
                }
            }

            Assert.Equal("tree", await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            using (System.IO.Pipes.NamedPipeClientStream silent = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut))
            {
                await silent.ConnectAsync(2000);
                using StreamReader reader = new StreamReader(silent);
                Assert.Contains("request too large or read failed", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)));
            }
        }

        [Fact]
        public async Task SecondServerOnTheSamePipeIsRefused()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            await using ControlServer first = new ControlServer(pipe, request => Task.FromResult(ControlResponse.Success()));
            await using ControlServer second = new ControlServer(pipe, request => Task.FromResult(ControlResponse.Success()));

            Assert.True(first.Start());
            Assert.False(second.Start());
        }

        [Fact]
        public async Task HandlerFailuresBecomeErrors()
        {
            string pipe = "agshell-test-" + Guid.NewGuid().ToString("N");
            await using ControlServer server = new ControlServer(pipe, request => throw new InvalidOperationException("boom"));
            server.Start();

            ControlReply reply = await ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "tree" }, TimeSpan.FromSeconds(5));

            Assert.False(reply.Response.Ok);
            Assert.Equal("internal error: boom", reply.Response.Error);
        }

        [Fact]
        public async Task MissingServerIsReportedPlainly()
        {
            ControlClientException error = await Assert.ThrowsAsync<ControlClientException>(() =>
                ControlClient.SendAsync("agshell-test-missing-" + Guid.NewGuid().ToString("N"), new ControlRequest { Cmd = "tree" }, TimeSpan.FromMilliseconds(200)));
            Assert.StartsWith("connect(", error.Message);
            Assert.Contains("AgShell may be stopped", error.Message);
            Assert.True(error.Connect);
        }

        [Fact]
        public async Task AMissingPipeFailsAtOnceAndTheStateDirectoryLockSaysWhetherAgShellIsThere()
        {
            string pipe = "agshell-test-missing-" + Guid.NewGuid().ToString("N");
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            ControlClientException stopped = await Assert.ThrowsAsync<ControlClientException>(() => ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "tree" }, TimeSpan.FromSeconds(10)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(@"connect(\\.\pipe\" + pipe + ") failed: The system cannot find the file specified — AgShell may be stopped or unable to accept connections", stopped.Message);

            string directory = Path.Combine(Path.GetTempPath(), "agshell-owner-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Contains("AgShell may be stopped", ControlClient.Hinted("p", "r", directory).Message);
                Assert.Equal(StateDirectoryClaim.Owned, StateDirectoryLock.Acquire(directory, out StateDirectoryLock? owner, out _));
                using (owner)
                {
                    Assert.Equal("connect(p) failed: r — the pipe owner is present but not accepting connections", ControlClient.Hinted("p", "r", directory).Message);
                    Assert.Contains("AgShell may be stopped", ControlClient.Hinted("p", "r", null).Message);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task ABusyPipeIsWaitedForThenNamed()
        {
            string pipe = "agshell-test-busy-" + Guid.NewGuid().ToString("N");
            using System.IO.Pipes.NamedPipeServerStream server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1);
            using System.IO.Pipes.NamedPipeClientStream taken = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
            Task accepted = server.WaitForConnectionAsync();
            await taken.ConnectAsync(2000);
            await accepted;
            ControlClientException busy = await Assert.ThrowsAsync<ControlClientException>(() => ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "tree" }, TimeSpan.FromMilliseconds(300)));
            Assert.EndsWith("failed: All pipe instances are busy — AgShell may be stopped or unable to accept connections", busy.Message);
        }

        [Fact]
        public async Task OversizedRequestNamesItsSizeAndTheCap()
        {
            ControlClientException error = await Assert.ThrowsAsync<ControlClientException>(() =>
                ControlClient.SendAsync("agshell-test-missing", new ControlRequest { Cmd = "session.type", Args = new ControlArgs { Text = new string('x', ControlEndpoint.MaxRequestBytes) } }, TimeSpan.FromMilliseconds(200)));
            Assert.Matches(@"^request too large \(\d+ bytes, cap " + ControlEndpoint.MaxRequestBytes + @"\) — split the input into smaller requests$", error.Message);
        }
    }
}
