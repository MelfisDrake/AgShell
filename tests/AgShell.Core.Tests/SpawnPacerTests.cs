using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class SpawnPacerTests
    {
        private DateTime clock = new DateTime(2026, 1, 1);
        private readonly List<(DateTime At, Action Run)> timers = new List<(DateTime, Action)>();
        private readonly List<string> granted = new List<string>();

        private SpawnPacer<string> Pacer()
        {
            SpawnPacer<string> pacer = new SpawnPacer<string>(TimeSpan.FromMilliseconds(120), () => clock, (delay, run) => timers.Add((clock + delay, run)));
            pacer.Granted += granted.Add;
            return pacer;
        }

        private void Advance(int milliseconds)
        {
            clock = clock.AddMilliseconds(milliseconds);
            foreach ((DateTime at, Action run) in timers.Where(timer => timer.At <= clock).ToList())
            {
                timers.Remove((at, run));
                run();
            }
        }

        [Fact]
        public void DrainFiresOnceWithTheTimeSinceArm()
        {
            SpawnPacer<string> pacer = Pacer();
            List<TimeSpan> drains = new List<TimeSpan>();
            pacer.Drained += drains.Add;
            Assert.True(pacer.Enqueue(new[] { "a", "b" }, new[] { "a" }));
            Assert.False(pacer.Enqueue(new[] { "c" }, Array.Empty<string>()));

            Assert.True(pacer.Request("a"));
            Assert.Empty(drains);
            clock = clock.AddMilliseconds(120);
            pacer.Discard("b");
            pacer.Discard("c");

            Assert.Equal(new[] { TimeSpan.FromMilliseconds(120) }, drains);
            Assert.True(pacer.IsPassthrough);
            pacer.Discard("a");
            Assert.Single(drains);
        }

        [Fact]
        public void OnScreenPanesGoAtOnceAndTheRestOneEvery120Milliseconds()
        {
            SpawnPacer<string> pacer = Pacer();
            pacer.Arm(new[] { "a", "b", "c" }, new[] { "b" });

            Assert.False(pacer.Request("a"));
            Assert.True(pacer.Request("b"));
            Assert.False(pacer.Request("c"));
            Assert.Empty(granted);

            Advance(120);
            Assert.Equal(new[] { "a" }, granted);
            Advance(60);
            Assert.Equal(new[] { "a" }, granted);
            Advance(60);
            Assert.Equal(new[] { "a", "c" }, granted);
            Assert.True(pacer.IsPassthrough);
            Assert.True(pacer.Request("new"));
        }

        [Fact]
        public void TheQueueDrainsInOrderAndExpeditedPanesSkipIt()
        {
            SpawnPacer<string> pacer = Pacer();
            pacer.Arm(new[] { "a", "b", "c" }, Array.Empty<string>());

            // c is ready first, but a heads the queue.
            Assert.False(pacer.Request("c"));
            Advance(500);
            Assert.Empty(granted);

            pacer.Expedite("c");
            Assert.Equal(new[] { "c" }, granted);
            pacer.Expedite("b");
            Assert.True(pacer.Request("b"));
            pacer.Discard("a");
            Assert.True(pacer.IsPassthrough);
        }
    }
}
