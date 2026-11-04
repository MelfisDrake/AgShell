using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class EventRingTests
    {
        [Fact]
        public void TheAppStreamRenumbersWindowEventsAndMarksTheirWindow()
        {
            ControlEventRing first = new ControlEventRing();
            ControlEventRing second = new ControlEventRing();
            ControlEventRing app = new ControlEventRing();
            first.Appended += controlEvent => app.Append(controlEvent, "w1");
            second.Appended += controlEvent => app.Append(controlEvent, "w2");
            string run = app.Read(null, null, null, 10).Batch.Run;

            first.Append(ControlEventKinds.SessionCreated, null, Guid.NewGuid(), new ControlEventPayload());
            second.Append(ControlEventKinds.SessionCreated, null, Guid.NewGuid(), new ControlEventPayload());
            first.Append(ControlEventKinds.Status, null, Guid.NewGuid(), new ControlEventPayload());

            ControlEventBatch batch = app.Read(run, 0, null, 10).Batch;
            Assert.Equal(new long[] { 1, 2, 3 }, batch.Items.Select(item => item.Seq));
            Assert.Equal(new[] { "w1", "w2", "w1" }, batch.Items.Select(item => item.Window));
            Assert.Equal(3, batch.Next);
            Assert.Single(app.Read(run, 0, new HashSet<string> { ControlEventKinds.Status }, 10).Batch.Items);
        }
    }
}
