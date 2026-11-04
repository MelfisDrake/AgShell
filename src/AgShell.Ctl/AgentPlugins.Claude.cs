using System.Text;

namespace AgShell.Ctl
{
    internal static partial class AgentPlugins
    {
        public static string? InstallClaude(AgentHome home)
        {
            if (!Directory.Exists(home.ClaudeDirectory) && home.Distribution is not null) return null;
            string? adapter = null;
            if (home.Distribution is not null)
            {
                string directory = home.UnixHome + "/" + ShellIntegrationDirectory;
                foreach (string name in new[] { "agshell-agent-status.sh", "agshell-claude-status.sh" })
                {
                    string contents = Installer.Resource(name).Replace("\r\n", "\n").Replace("__AGSHELLCTL__", ShellQuote(home.AgShellctlPath));
                    WriteText(home.ResolveAbsolute(directory + "/" + name), contents);
                    Installer.RunWsl("-d " + home.Distribution + " -e chmod +x \"" + directory + "/" + name + "\"", Encoding.UTF8);
                }

                adapter = ShellQuote(directory + "/agshell-claude-status.sh");
            }

            return HooksInstaller.InstallInto(home.Resolve(".claude/settings.json"), home.AgShellctlCommand,
                home.OriginalPath(".claude/settings.json") + ".bak", adapter);
        }
    }
}
