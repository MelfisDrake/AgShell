namespace AgShell.Ctl.Tests
{
    public class AgentProcessesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "agshell-agents-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void AnNpmScriptIsToldByItsBinName()
        {
            string package = Path.Combine(root, "node_modules", "@anthropic-ai", "claude-code");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@anthropic-ai/claude-code\",\"bin\":{\"claude\":\"cli.js\"}}");
            string codex = Path.Combine(root, "node_modules", "@openai", "codex");
            Directory.CreateDirectory(Path.Combine(codex, "bin"));
            File.WriteAllText(Path.Combine(codex, "package.json"), "{\"name\":\"@openai/codex\",\"bin\":\"bin/codex.js\"}");

            Assert.Equal(new[] { "claude" }, AgentProcesses.NpmCommands(Path.Combine(package, "cli.js")));
            Assert.Empty(AgentProcesses.NpmCommands(Path.Combine(package, "other.js")));
            Assert.Equal(new[] { "codex" }, AgentProcesses.NpmCommands(Path.Combine(codex, "bin", "codex.js")));
        }

        [Fact]
        public void TheCommandLineSplitsAsArgv()
        {
            Assert.Equal(new[] { "C:\\nodejs\\node.exe", "C:\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js", "-p" },
                AgentProcesses.SplitArguments("\"C:\\nodejs\\node.exe\"  \"C:\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" -p"));
        }

        [Fact]
        public void AgentReAddsNamesAndABadOneAddsNothing()
        {
            try
            {
                Assert.False(AgentProcesses.IsAgent("qwen"));
                Environment.SetEnvironmentVariable("AGSHELL_AGENT_RE", "^qw");
                Assert.True(AgentProcesses.IsAgent("qwen"));
                Assert.True(AgentProcesses.IsAgent("claude"));
                Environment.SetEnvironmentVariable("AGSHELL_AGENT_RE", "(");
                Assert.False(AgentProcesses.IsAgent("qwen"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("AGSHELL_AGENT_RE", null);
            }
        }
    }
}
