using System.Text;

namespace AgShell.Core.Tests
{
    public class CommandFailureTests
    {
        [Fact]
        public void TheDetailIsTheLastLineLeftOnceCleaned()
        {
            Assert.Equal("fatal: no remote", HudSpec.TailDetail(Encoding.UTF8.GetBytes("warning\r\nfatal: no remote\r\n\u001b[0m\n")));
            Assert.Null(HudSpec.TailDetail(Encoding.UTF8.GetBytes("\n\u001b[31m\n")));
            Assert.Null(HudSpec.TailDetail(Array.Empty<byte>()));
        }

        [Fact]
        public void ACutCharacterDoesNotSpoilTheDetail()
        {
            byte[] text = Encoding.UTF8.GetBytes("ошибка доступа");
            Assert.EndsWith("шибка доступа", HudSpec.TailDetail(text[1..]));
        }
    }
}
