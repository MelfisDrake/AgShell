using System.Text;
using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class TerminalClipboardTests
    {
        private const string St = "\u001b\\";

        private static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        [Fact]
        public void Osc52ReadsAndWritesAsGhosttyParsesThem()
        {
            Assert.Null(Osc52.Parse("c;?")!.Data);
            Assert.Equal(ClipboardLocation.Standard, Osc52.Parse(";?")!.Location);
            Assert.Equal(ClipboardLocation.Selection, Osc52.Parse("s;?")!.Location);
            Assert.Equal(ClipboardLocation.Primary, Osc52.Parse("p;?")!.Location);
            Assert.Equal("hi", Encoding.UTF8.GetString(Osc52.Parse("c;aGk=")!.Data!));
            // Padding may be missing, characters outside the alphabet may not, and two kind letters are not one.
            Assert.Equal("hi", Encoding.UTF8.GetString(Osc52.Parse("c;aGk")!.Data!));
            Assert.Null(Osc52.Parse("c;aG k="));
            Assert.Null(Osc52.Parse("cs;aGk="));
            Assert.Null(Osc52.Parse(""));
            Assert.Empty(Osc52.Parse("c;")!.Data!);
        }

        [Fact]
        public void Osc52ReplyCarriesTheTextOrNothing()
        {
            Assert.Equal("\u001b]52;c;aGk=" + St, Osc52.Reply(ClipboardLocation.Standard, "hi"));
            Assert.Equal("\u001b]52;s;" + St, Osc52.Reply(ClipboardLocation.Selection, ""));
        }

        [Fact]
        public void StrictBase64RefusesWhatGhosttyRefuses()
        {
            Assert.Equal("text/plain", Encoding.UTF8.GetString(StrictBase64.Decode("dGV4dC9wbGFpbg==", true)!));
            Assert.Null(StrictBase64.Decode("dGV4dC9wbGFpbg", true));
            Assert.Null(StrictBase64.Decode("dGV4dC9wbGFpbg=!", true));
            Assert.Null(StrictBase64.Decode("dGV4dC9w\nbGFpbg==", true));
            // Non-zero padding bits are not the same data.
            Assert.Null(StrictBase64.Decode("aGl=", true));
        }

        [Fact]
        public void MalformedMetadataIsDroppedAndTheIdIsCleaned()
        {
            Assert.Null(KittyClipboard.ParseMetadata(""));
            Assert.Null(KittyClipboard.ParseMetadata("type=read:bare"));
            Assert.Null(KittyClipboard.ParseMetadata("loc=primary"));
            Assert.Null(KittyClipboard.ParseMetadata("type=bobr"));
            Assert.Equal("read", KittyClipboard.ParseMetadata("type=bobr:type=read")!.Op);
            Assert.Equal("42", KittyClipboard.ParseMetadata("type=read:id=*4 2*")!.Id);
            Assert.True(KittyClipboard.ParseMetadata("type=read:loc=primary")!.Primary);
            Assert.Equal("text/plain", KittyClipboard.ParseMetadata("type=wdata:mime=dGV4dC9wbGFpbg==")!.Mime);
            Assert.Throws<FormatException>(() => KittyClipboard.ParseMetadata("type=wdata:mime=!!!"));
        }

        [Fact]
        public void ReadListsTypesAndKeepsFourOfThem()
        {
            KittyClipboard clipboard = new KittyClipboard();
            KittyClipboardRead read = clipboard.Handle("type=read:id=r1;" + B64(". text/plain a b c d"), false).Read!;

            Assert.True(read.List);
            Assert.Equal(new[] { "text/plain", "a", "b", "c" }, read.Mimes);
            Assert.Equal("r1", read.Id);
            Assert.False(read.Granted);
            Assert.True(KittyClipboard.ReadNeedsClipboard(read));
            // An undecodable list is dropped without an answer.
            Assert.Equal((null, null, null), clipboard.Handle("type=read;!!", false));
        }

        [Fact]
        public void ReadReplyServesTextOnlyToATextPlainRead()
        {
            KittyClipboardRead read = new KittyClipboardRead(false, new[] { "UTF8_STRING", "text/plain" }, true, "x", false);
            string reply = KittyClipboard.ReadReply(read, "hi", new[] { "text/plain" });

            Assert.Equal(
                "\u001b]5522;type=read:status=OK:id=x" + St
                + "\u001b]5522;type=read:status=DATA:id=x:mime=" + B64(".") + ";" + B64("text/plain\n") + St
                + "\u001b]5522;type=read:status=DATA:id=x:mime=" + B64("UTF8_STRING") + ";" + B64("hi") + St
                + "\u001b]5522;type=read:status=DATA:id=x:mime=" + B64("text/plain") + ";" + B64("hi") + St
                + "\u001b]5522;type=read:status=DONE:id=x" + St,
                reply);

            KittyClipboardRead other = new KittyClipboardRead(false, new[] { "UTF8_STRING" }, false, "", false);
            Assert.False(KittyClipboard.ReadNeedsClipboard(other));
            Assert.Equal("\u001b]5522;type=read:status=OK" + St + "\u001b]5522;type=read:status=DONE" + St, KittyClipboard.ReadReply(other, "hi", Array.Empty<string>()));
        }

        [Fact]
        public void ReadDataGoesOutInChunksOf4096Bytes()
        {
            KittyClipboardRead read = new KittyClipboardRead(false, new[] { "text/plain" }, false, "", false);
            string reply = KittyClipboard.ReadReply(read, new string('a', 5000), Array.Empty<string>());

            Assert.Equal(2, reply.Split("status=DATA").Length - 1);
        }

        [Fact]
        public void WriteGathersChunksAliasesAndCommits()
        {
            KittyClipboard clipboard = new KittyClipboard();
            string text = B64("hello world");

            Assert.Equal((null, null, null), clipboard.Handle("type=write:id=w", false));
            Assert.Null(clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";" + text[..5], false).Reply);
            Assert.Null(clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";" + text[5..], false).Reply);
            Assert.Null(clipboard.Handle("type=walias:mime=" + B64("text/plain") + ";" + B64("UTF8_STRING"), false).Reply);
            KittyClipboardWrite write = clipboard.Handle("type=wdata", false).Write!;

            Assert.Equal("w", write.Id);
            Assert.Equal(new[] { "text/plain", "UTF8_STRING" }, write.Contents.Select(content => content.Mime));
            Assert.All(write.Contents, content => Assert.Equal("hello world", Encoding.UTF8.GetString(content.Data)));
            // The transaction is over: a second commit is ignored.
            Assert.Equal((null, null, null), clipboard.Handle("type=wdata", false));
        }

        [Fact]
        public void PaddingSplitAcrossPacketsIsOneStream()
        {
            KittyClipboard clipboard = new KittyClipboard();
            clipboard.Handle("type=write", false);
            clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";aGk", false);
            clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";=", false);

            Assert.Equal("hi", Encoding.UTF8.GetString(clipboard.Handle("type=wdata", false).Write!.Contents[0].Data));
        }

        [Fact]
        public void AnswersEndWithTheRequestsTerminator()
        {
            KittyClipboard clipboard = new KittyClipboard();

            Assert.Equal("\u001b]5522;type=write:status=EPERM:id=a\u0007", clipboard.Handle("type=write:id=a", true, KittyClipboard.Bel).Reply);
            KittyClipboardRead read = clipboard.Handle("type=read:id=r;" + B64("text/plain"), false, KittyClipboard.Bel).Read!;
            Assert.Equal("\u001b]5522;type=read:status=OK:id=r\u0007\u001b]5522;type=read:status=DATA:id=r:mime=" + B64("text/plain") + ";" + B64("hi") + "\u0007\u001b]5522;type=read:status=DONE:id=r\u0007", KittyClipboard.ReadReply(read, "hi", Array.Empty<string>()));
            clipboard.Handle("type=write:id=w", false, KittyClipboard.Bel);
            clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";" + B64("hi"), false, KittyClipboard.Bel);
            Assert.Equal(St, clipboard.Handle("type=wdata", false).Write!.Terminator);
        }

        [Fact]
        public void BrokenWritesFailWithTheirStatus()
        {
            KittyClipboard clipboard = new KittyClipboard();

            Assert.Equal("\u001b]5522;type=write:status=EPERM:id=a" + St, clipboard.Handle("type=write:id=a", true).Reply);

            clipboard.Handle("type=write:id=b", false);
            Assert.Equal("\u001b]5522;type=write:status=EINVAL:id=b" + St, clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";a b", false).Reply);
            // After an error the write's later packets are ignored.
            Assert.Equal((null, null, null), clipboard.Handle("type=wdata", false));

            clipboard.Handle("type=write:id=c", false);
            clipboard.Handle("type=wdata:mime=" + B64("text/plain") + ";aG", false);
            Assert.Equal("\u001b]5522;type=write:status=EINVAL:id=c" + St, clipboard.Handle("type=wdata", false).Reply);

            clipboard.Handle("type=write:id=d", false);
            Assert.Equal("\u001b]5522;type=write:status=EINVAL:id=d" + St, clipboard.Handle("type=wdata:mime=!!;aGk=", false).Reply);

            clipboard.Handle("type=write:id=e", false);
            Assert.Equal("\u001b]5522;type=write:status=EINVAL:id=e" + St, clipboard.Handle("type=walias;" + B64("x"), false).Reply);
        }

        [Fact]
        public void PasteEventPasswordGrantsOneRead()
        {
            KittyClipboard clipboard = new KittyClipboard();
            string paste = clipboard.PasteEvent(new[] { "text/plain" });
            string pw = paste.Split(":pw=")[1].Split(new[] { ';', '\u001b' })[0];

            Assert.Contains("type=read:status=OK:pw=" + pw, paste);
            Assert.Contains(";" + B64("text/plain\n"), paste);
            string request = "type=read:pw=" + pw + ":name=" + B64("Paste event") + ";" + B64("text/plain");
            Assert.True(clipboard.Handle(request, false).Read!.Granted);
            Assert.False(clipboard.Handle(request, false).Read!.Granted);
        }

        [Fact]
        public void APasswordWithoutANameIsNoPasswordAndAListingSpendsNothing()
        {
            KittyClipboard clipboard = new KittyClipboard();
            string paste = clipboard.PasteEvent(Array.Empty<string>());
            string pw = paste.Split(":pw=")[1].Split(new[] { ';', '\u001b' })[0];

            Assert.False(clipboard.Handle("type=read:pw=" + pw + ";" + B64("text/plain"), false).Read!.Granted);
            Assert.False(clipboard.Handle("type=read:pw=" + pw + ":name=" + B64("n") + ";" + B64("."), false).Read!.Granted);
            Assert.True(clipboard.Handle("type=read:pw=" + pw + ":name=" + B64("n") + ";" + B64("text/plain"), false).Read!.Granted);
        }

        [Fact]
        public void PromptPolicyRemembersEachDirectionApart()
        {
            ClipboardPromptPolicy policy = new ClipboardPromptPolicy();
            policy.Remember(ClipboardAccess.Write, true);

            Assert.Equal(ClipboardDecision.Allow, policy.Decision(ClipboardAccess.Write));
            Assert.Equal(ClipboardDecision.Prompt, policy.Decision(ClipboardAccess.Read));
            policy.Remember(ClipboardAccess.Read, false);
            Assert.Equal(ClipboardDecision.Deny, policy.Decision(ClipboardAccess.Read));
        }
    }
}
