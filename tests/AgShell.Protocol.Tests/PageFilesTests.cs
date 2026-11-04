using System.Text.Json;

namespace AgShell.Protocol.Tests
{
    public class PageFilesTests
    {
        [Fact]
        public void APageReadsItsOwnFileAndWithARootOnlyWhatIsInsideIt()
        {
            string file = @"C:\work\site\index.html";
            Assert.Equal(file, PageFiles.Resolve(file, null, "/index.html"));
            Assert.Null(PageFiles.Resolve(file, null, "/style.css"));
            Assert.Equal(@"C:\work\site\css\style.css", PageFiles.Resolve(file, @"C:\work\site", "/css/style.css"));
            Assert.Null(PageFiles.Resolve(file, @"C:\work\site", "/../secret.txt"));
            Assert.Null(PageFiles.Resolve(file, @"C:\work\site", "/../site-other/x"));
        }

        [Fact]
        public void APageInAFolderOfTheRootReadsWhatIsAboveItInTheRoot()
        {
            string file = @"C:\work\site\pages\report one.html";
            Assert.Equal("/pages/report%20one.html", PageFiles.AddressOf(file, @"C:\work\site"));
            Assert.Equal("/report%20one.html", PageFiles.AddressOf(file, null));
            Assert.Equal(file, PageFiles.Resolve(file, @"C:\work\site", "/pages/report one.html"));
            Assert.Equal(@"C:\work\site\style.css", PageFiles.Resolve(file, @"C:\work\site", "/style.css"));
            Assert.Equal(file, PageFiles.Resolve(file, null, "/report one.html"));
        }

        [Fact]
        public void APageFileHasTheWebsTypeAndAnUnknownOneTheRegistrysOrBytes()
        {
            Assert.Equal("video/webm", PageFiles.ContentType(@"C:\site\clip.webm"));
            Assert.Equal("video/mp4", PageFiles.ContentType(@"C:\site\CLIP.MP4"));
            Assert.Equal("audio/mpeg", PageFiles.ContentType(@"C:\site\a.mp3"));
            Assert.Equal("text/javascript; charset=utf-8", PageFiles.ContentType(@"C:\site\app.mjs"));
            Assert.Equal("application/wasm", PageFiles.ContentType(@"C:\site\a.wasm"));
            Assert.Equal("text/csv; charset=utf-8", PageFiles.ContentType(@"C:\site\a.csv"));
            Assert.Equal("application/octet-stream", PageFiles.ContentType(@"C:\site\a.agshell-unknown"));
            Assert.Equal("application/octet-stream", PageFiles.ContentType(@"C:\site\noextension"));
        }

        [Fact]
        public void AJunctionLeadingOutOfTheFolderIsRefused()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                string site = Directory.CreateDirectory(Path.Combine(folder, "site")).FullName;
                string outside = Directory.CreateDirectory(Path.Combine(folder, "outside")).FullName;
                File.WriteAllText(Path.Combine(site, "index.html"), "x");
                File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
                // A junction, which needs no privilege, unlike a directory symbolic link.
                using (System.Diagnostics.Process mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(site, "link") + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
                {
                    mklink.WaitForExit();
                    Assert.Equal(0, mklink.ExitCode);
                }

                string page = Path.Combine(site, "index.html");
                Assert.Equal(page, PageFiles.ResolveReal(page, site, "/index.html"));
                Assert.NotNull(PageFiles.Resolve(page, site, "/link/secret.txt"));
                Assert.Null(PageFiles.ResolveReal(page, site, "/link/secret.txt"));
            }
            finally
            {
                string link = Path.Combine(folder, "site", "link");
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }

                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void AFrameRoundTripsAndAnIncompleteOrOversizedOneIsRefused()
        {
            ControlPageServeFrame chunk = PageServe.Decode(PageServe.Encode(new ControlPageServeFrame { Kind = PageServeKinds.Chunk, Id = 3, Data = new byte[] { 1, 2 } }).TrimEnd('\n'));
            Assert.Equal(3, chunk.Id);
            Assert.Equal(new byte[] { 1, 2 }, chunk.Data);
            Assert.Throws<JsonException>(() => PageServe.Decode("{\"kind\":\"head\",\"id\":1}"));
            Assert.Throws<JsonException>(() => PageServe.Decode("{\"kind\":\"nope\",\"id\":1}"));
            Assert.Throws<JsonException>(() => PageServe.Encode(new ControlPageServeFrame { Kind = PageServeKinds.Chunk, Id = 1, Data = new byte[PageServe.MaxChunkBytes + 1] }));
            Assert.Equal(PageServeKinds.Chunk, PageServe.Decode(PageServe.Encode(new ControlPageServeFrame { Kind = PageServeKinds.Chunk, Id = 1, Data = new byte[PageServe.MaxChunkBytes] }).TrimEnd('\n')).Kind);
        }
    }
}
