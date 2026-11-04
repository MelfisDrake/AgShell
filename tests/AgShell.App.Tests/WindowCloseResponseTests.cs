using AgShell.Protocol;

namespace AgShell.App.Tests
{
    public class WindowCloseResponseTests
    {
        [Fact]
        public async Task LastWindowShutdownWaitsForItsReplyOverThePipe()
        {
            WindowCloseResponse closing = new WindowCloseResponse();
            bool closed = false;
            TaskCompletionSource shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string pipe = "agshell-close-test-" + Guid.NewGuid().ToString("N");
            await using ControlServer server = new ControlServer(pipe, request =>
            {
                closing.Close(request, () =>
                {
                    closed = true;
                    Assert.True(closing.DeferShutdown(() => shutdown.SetResult()));
                });
                Assert.False(shutdown.Task.IsCompleted);
                return Task.FromResult(ControlResponse.Success(new ControlResult { Id = "closed-window" }));
            });
            server.ReplyCompleted += (request, response) => closing.Complete(request);
            Assert.True(server.Start());

            ControlReply reply = await ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "window.close" }, TimeSpan.FromSeconds(5));

            Assert.True(closed);
            Assert.True(reply.Response.Ok);
            Assert.Equal("closed-window", reply.Response.Result!.Id);
            await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void AnotherReplyCannotReleaseShutdownAndCompletionRunsOnlyOnce()
        {
            WindowCloseResponse closing = new WindowCloseResponse();
            ControlRequest request = new ControlRequest { Cmd = "window.close" };
            int shutdowns = 0;
            closing.Close(request, () => Assert.True(closing.DeferShutdown(() => shutdowns++)));

            closing.Complete(new ControlRequest { Cmd = "window.close" });
            Assert.Equal(0, shutdowns);
            closing.Complete(request);
            closing.Complete(request);
            Assert.Equal(1, shutdowns);
        }

        [Fact]
        public void ClosingOneOfSeveralWindowsDoesNotScheduleShutdown()
        {
            WindowCloseResponse closing = new WindowCloseResponse();
            ControlRequest request = new ControlRequest { Cmd = "window.close" };
            closing.Close(request, () => { });
            closing.Complete(request);
            Assert.False(closing.DeferShutdown(() => Assert.Fail("Ordinary shutdown must not be deferred")));
        }

        [Fact]
        public void FailedCloseDoesNotDeferLaterOrdinaryShutdown()
        {
            WindowCloseResponse closing = new WindowCloseResponse();
            Assert.Throws<InvalidOperationException>(() => closing.Close(new ControlRequest { Cmd = "window.close" }, () => throw new InvalidOperationException()));
            Assert.False(closing.DeferShutdown(() => Assert.Fail("Shutdown must not be deferred")));
        }
    }
}
