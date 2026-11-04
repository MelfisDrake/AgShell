using AgShell.Protocol;

namespace AgShell.App.Tests
{
    public class ModalCloseDrainTests
    {
        [Fact]
        public async Task BothCancelledRepliesReachTheirClientsBeforeShutdown()
        {
            ModalCloseDrain drain = new ModalCloseDrain();
            drain.Track("ask", "a");
            drain.Track("pick", "p");
            TaskCompletionSource completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(drain.Begin(() => completed.SetResult()));
            string pipe = "agshell-modal-drain-" + Guid.NewGuid().ToString("N");
            await using ControlServer server = new ControlServer(pipe, request => Task.FromResult(Cancelled(request.Cmd)));
            server.Written += drain.Written;
            Assert.True(server.Start());

            ControlReply ask = await ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "ask.result", Target = "a" }, TimeSpan.FromSeconds(5));
            Assert.Equal("cancelled", ask.Response.Result!.Ask!.Result);
            Assert.False(completed.Task.IsCompleted);
            ControlReply pick = await ControlClient.SendAsync(pipe, new ControlRequest { Cmd = "pick.result", Target = "p" }, TimeSpan.FromSeconds(5));
            Assert.Equal("cancelled", pick.Response.Result!.Pick!.Result);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void OnlyAMatchingWrittenCancellationFinishesTheDrain()
        {
            ModalCloseDrain drain = new ModalCloseDrain();
            drain.Track("ask", "a");
            int completed = 0;
            Assert.True(drain.Begin(() => completed++));
            ControlRequest request = new ControlRequest { Cmd = "ask.result", Target = "a" };
            drain.Written(request, new ControlResponse { Ok = true, Result = new ControlResult { Ask = new ControlAskResult { Result = "pending" } } });
            drain.Written(request, ControlResponse.Failure("refused"));
            drain.Written(request with { Target = "other" }, Cancelled("ask.result"));
            drain.Written(request with { Cmd = "ask.cancel" }, Cancelled("ask.result"));
            Assert.Equal(0, completed);
            drain.Written(request, Cancelled("ask.result"));
            drain.Written(request, Cancelled("ask.result"));
            drain.Expire();
            Assert.Equal(1, completed);
        }

        [Fact]
        public void MissingClientHasABoundedDeadlineAndNoDialogsNeedsNoDelay()
        {
            ModalCloseDrain drain = new ModalCloseDrain();
            drain.Track("pick", "abandoned");
            int completed = 0;
            Assert.True(drain.Begin(() => completed++));
            Assert.Equal(TimeSpan.FromSeconds(2), ModalCloseDrain.GracePeriod);
            drain.Expire();
            Assert.Equal(1, completed);
            Assert.False(drain.Begin(() => completed++));
            Assert.Equal(2, completed);
        }

        private static ControlResponse Cancelled(string command)
        {
            return new ControlResponse
            {
                Ok = true,
                Result = command == "ask.result"
                    ? new ControlResult { Ask = new ControlAskResult { Result = "cancelled" } }
                    : new ControlResult { Pick = new ControlPickResult { Result = "cancelled" } },
            };
        }
    }
}
