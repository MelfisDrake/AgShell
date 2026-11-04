namespace AgShell.Protocol.Tests
{
    public class RemoteTreeTests
    {
        [Fact]
        public void AnEarlierAgShellsTreeIsRead()
        {
            string line = "{\"ok\":true,\"result\":{\"remote\":{\"sessions\":[{\"id\":\"s\",\"name\":\"n\",\"workspace\":\"w\",\"window\":\"main\",\"panes\":[{\"pane\":\"left\",\"daemon\":\"s:Main\",\"foreground\":\"\\\"C:\\\\Program Files\\\\x.exe\\\" --flag\"}]}]}}}";

            ControlRemoteSession session = ControlJson.DeserializeResponse(line).Result!.Remote!.Sessions.Single();

            Assert.Equal("w", session.Workspace);
            Assert.Equal("main", session.Window);
            Assert.Equal(new[] { "C:\\Program Files\\x.exe", "--flag" }, session.Panes!.Single().Foreground);
        }

        [Fact]
        public void TheCurrentTreeWritesOnlyAgtermsNames()
        {
            ControlResponse response = ControlResponse.Success(new ControlResult
            {
                Remote = new ControlRemoteTree
                {
                    Sessions = new List<ControlRemoteSession>
                    {
                        new ControlRemoteSession { Id = "s", Name = "n", Workspace = "w", Window = "main", Panes = new List<ControlRemotePane> { new ControlRemotePane { Pane = "left", Daemon = "s:Main", Foreground = new List<string> { "x", "y" } } } },
                    },
                },
            });

            string line = ControlJson.Serialize(response);

            Assert.Contains("\"workspaceName\":\"w\"", line);
            Assert.DoesNotContain("\"workspace\":", line);
            Assert.Contains("\"foreground\":[\"x\",\"y\"]", line);
            Assert.Equal("w", ControlJson.DeserializeResponse(line).Result!.Remote!.Sessions.Single().Workspace);
        }
    }
}
