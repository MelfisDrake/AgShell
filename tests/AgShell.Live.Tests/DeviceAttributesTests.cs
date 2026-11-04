namespace AgShell.Live.Tests
{
    public class DeviceAttributesTests
    {
        [Theory]
        [InlineData("\x1b[c", DeviceAttributes.PrimaryReply)]
        [InlineData("\x1b[0c", DeviceAttributes.PrimaryReply)]
        [InlineData("\x1b[>c", DeviceAttributes.SecondaryReply)]
        [InlineData("\x1b[>0c", DeviceAttributes.SecondaryReply)]
        public void AQueryGetsItsReply(string query, string reply)
        {
            Assert.Equal(reply, DeviceAttributes.Respond("prompt" + query + "> "));
        }

        [Fact]
        public void EveryQueryInARead()
        {
            Assert.Equal(DeviceAttributes.PrimaryReply + DeviceAttributes.SecondaryReply, DeviceAttributes.Respond("\x1b[c text \x1b[>c"));
        }

        [Fact]
        public void AReplyIsNoQuery()
        {
            Assert.Equal(string.Empty, DeviceAttributes.Respond("\x1b[?62;22c\x1b[?1;2c"));
        }

        [Fact]
        public void OtherSequencesAreNoQuery()
        {
            Assert.Equal(string.Empty, DeviceAttributes.Respond("\x1b[2J\x1b[1;1H\x1b[31mred\x1b[0m\x1b[6n"));
        }
    }
}
