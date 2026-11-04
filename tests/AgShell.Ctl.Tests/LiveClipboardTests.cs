using AgShell.Protocol;

namespace AgShell.Ctl.Tests
{
    public class LiveClipboardTests
    {
        private const string State = @"D:\test-state";

        [Fact]
        public void ADaemonOfTheStateIsTaken()
        {
            string pipe = ControlEndpoint.LivePanePipe(State, Guid.NewGuid().ToString());

            Assert.Equal(pipe, LiveClipboard.DaemonPipe(pipe, State));
        }

        [Fact]
        public void APipeOfAnotherStateOrShapeIsNot()
        {
            string other = ControlEndpoint.LivePanePipe(@"D:\other-state", Guid.NewGuid().ToString());

            Assert.Null(LiveClipboard.DaemonPipe(null, State));
            Assert.Null(LiveClipboard.DaemonPipe(string.Empty, State));
            Assert.Null(LiveClipboard.DaemonPipe(other, State));
            Assert.Null(LiveClipboard.DaemonPipe(ControlEndpoint.LivePipePrefix(State) + "notes", State));
            Assert.Null(LiveClipboard.DaemonPipe(ControlEndpoint.LivePipePrefix(State) + new string('A', 32), State));
        }
    }
}
