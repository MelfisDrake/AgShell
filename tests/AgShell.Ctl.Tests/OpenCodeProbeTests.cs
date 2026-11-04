using System.Diagnostics;

namespace AgShell.Ctl.Tests
{
    public class OpenCodeProbeTests
    {
        private const string Marker = "agshell-opencode-version-test";

        [Theory]
        [InlineData("1.4.2\n", 1)]
        [InlineData("opencode v2.0.0-beta.1+build.5", 2)]
        [InlineData("  v2.1.0  ", 2)]
        [InlineData("3.0.0", null)]
        [InlineData("1.4", null)]
        [InlineData("welcome\n1.4.2", null)]
        [InlineData("", null)]
        public void TheVersionIsReadAsAgtermReadsIt(string output, int? major)
        {
            Assert.Equal(major, OpenCodeProbe.ParseVersion(output));
        }

        [Fact]
        public void TheConfigurationDirectoryFollowsTheOverrideThenXdg()
        {
            Assert.Equal("/home/me/.config/opencode", OpenCodeProbe.ConfigurationDirectory("/home/me", null, null, "/work", posix: true));
            Assert.Equal("/home/me/.config/opencode", OpenCodeProbe.ConfigurationDirectory("/home/me", null, string.Empty, "/work", posix: true));
            Assert.Equal("/xdg/opencode", OpenCodeProbe.ConfigurationDirectory("/home/me", null, "/xdg", "/work", posix: true));
            Assert.Equal("/work/rel/opencode", OpenCodeProbe.ConfigurationDirectory("/home/me", null, "rel", "/work", posix: true));
            Assert.Equal("/etc/oc", OpenCodeProbe.ConfigurationDirectory("/home/me", "/etc/./x/../oc/", "/xdg", "/work", posix: true));
            Assert.Equal("/work/oc", OpenCodeProbe.ConfigurationDirectory("/home/me", "oc", null, "/work", posix: true));
            Assert.Null(OpenCodeProbe.ConfigurationDirectory("/home/me", string.Empty, "/xdg", "/work", posix: true));
            Assert.Equal(@"C:\Users\me\.config\opencode", OpenCodeProbe.ConfigurationDirectory(@"C:\Users\me", null, null, @"D:\work", posix: false));
            Assert.Equal(@"D:\work\oc", OpenCodeProbe.ConfigurationDirectory(@"C:\Users\me", "oc", null, @"D:\work", posix: false));
        }

        [Fact]
        public void TheProbeOutputIsReadAfterTheMarkerOnly()
        {
            string output = "rc noise\n" + "\n" + Marker + "\n" + "/home/me\0/home/me\0x\0/cfg\0/xdg\0" + "opencode 2.0.1\n";
            OpenCodeProbe.Result? result = OpenCodeProbe.Parse(output, Marker, succeeded: true, "/fallback");
            Assert.Equal(new OpenCodeProbe.Result(2, "/cfg"), result);

            Assert.Equal(new OpenCodeProbe.Result(null, "/cfg"), OpenCodeProbe.Parse(output, Marker, succeeded: false, "/fallback"));
            Assert.Equal(new OpenCodeProbe.Result(1, "/fallback/.config/opencode"), OpenCodeProbe.Parse("\n" + Marker + "\n\0/\0\0\0\0" + "1.0.0", Marker, true, "/fallback"));
            Assert.Null(OpenCodeProbe.Parse("1.0.0", Marker, true, "/fallback"));
            Assert.Null(OpenCodeProbe.Parse("\n" + Marker + "\n/home/me\0/\0", Marker, true, "/fallback"));
        }

        [Fact]
        public void TheWslProbeRunsTheLoginShellInteractively()
        {
            IReadOnlyList<string> arguments = OpenCodeProbe.WslArguments("Ubuntu", Marker);
            Assert.Equal(new[] { "-d", "Ubuntu", "-e", "sh", "-c" }, arguments.Take(5));
            Assert.Contains("-ilc \"$1\"", arguments[5]);
            Assert.Equal("sh", arguments[6]);
            Assert.StartsWith("exec /bin/sh -c '/usr/bin/printf '\\''\\n" + Marker, arguments[7]);
            Assert.EndsWith("exec /usr/bin/env opencode --version'", arguments[7]);
        }

        [Fact]
        public void TheProbeLeavesTheSessionVariablesOut()
        {
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe");
            start.Environment["AGSHELL_SESSION_ID"] = "x";
            start.Environment["AGSHELL_PANE_ID"] = "y";
            start.Environment["OPENCODE_CONFIG_DIR"] = "z";
            OpenCodeProbe.Isolate(start);
            Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("AGSHELL_", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("z", start.Environment["OPENCODE_CONFIG_DIR"]);
        }

        [Fact]
        public void TheVersionPicksTheDirectoryAndAnUnknownOneAsksForAChoice()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-opencode-" + Guid.NewGuid());
            string custom = Path.Combine(root, "custom");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".config", "opencode"));
                Directory.CreateDirectory(custom);
                AgentHome home = new AgentHome("Windows", root, null, string.Empty, "C:/agshell/agshellctl.exe", "\"C:/agshell/agshellctl.exe\"");

                bool needed = false;
                Assert.StartsWith("skipped: ", AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(null, custom), null, ref needed));
                Assert.True(needed);

                needed = false;
                Assert.Equal("status plugin installation was skipped", AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(null, custom), 0, ref needed));
                Assert.False(needed);

                AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(2, custom), null, ref needed);
                Assert.True(File.Exists(Path.Combine(custom, "plugins", "agshell-v2", "tui.js")));

                AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(null, custom), 1, ref needed);
                Assert.True(File.Exists(Path.Combine(root, ".config", "opencode", "plugins", "agshell-status.js")));
                Assert.False(needed);

                Assert.Throws<InvalidOperationException>(() => AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(2, null), null, ref needed));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void NoConfigAnywhereMeansOpenCodeIsNotSetUp()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-opencode-" + Guid.NewGuid());
            AgentHome home = new AgentHome("Windows", root, null, string.Empty, "C:/agshell/agshellctl.exe", "\"C:/agshell/agshellctl.exe\"");
            bool needed = false;
            Assert.Null(AgentPlugins.InstallOpenCode(home, null, null, ref needed));
            Assert.Null(AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(null, Path.Combine(root, "none")), null, ref needed));
            Assert.Null(AgentPlugins.InstallOpenCode(home, new OpenCodeProbe.Result(2, Path.Combine(root, "none")), null, ref needed));
            Assert.False(needed);
        }
    }
}
