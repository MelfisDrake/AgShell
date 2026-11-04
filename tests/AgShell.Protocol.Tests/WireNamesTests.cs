namespace AgShell.Protocol.Tests
{
    public class WireNamesTests
    {
        [Fact]
        public void ArgumentsTravelUnderAgtermsNames()
        {
            string line = ControlJson.Serialize(new ControlRequest
            {
                Cmd = "x",
                Args = new ControlArgs { Js = true, PageId = "p", PaneId = "q", Repeat = true, Value = "v", Path = "/a.png", Color = "#112233" },
            });
            foreach (string name in new[] { "\"javascript\":true", "\"page\":\"p\"", "\"paneID\":\"q\"", "\"repeats\":true", "\"value\":\"v\"", "\"path\":\"/a.png\"", "\"color\":\"#112233\"" })
            {
                Assert.Contains(name, line);
            }

            ControlArgs back = ControlJson.DeserializeRequest(line).Args!;
            Assert.Equal((true, "p", "q", true), (back.Js, back.PageId, back.PaneId, back.Repeat));
        }
    }
}
