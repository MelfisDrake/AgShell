using System.Text;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's ShellEscape for a dropped or pasted path: one literal argument for the pane's shell, and a path with no
    /// character that shell treats specially comes back unchanged, so an agent reads a clean path out of its prompt.
    /// </summary>
    public static class PastedPath
    {
        // agterm's list, backslash first so its own pass does not double the backslashes added for the rest.
        private static readonly char[] PosixSignificant = "\\ ()[]{}<>\"'`!#$&;|*?\t\n\r".ToCharArray();

        // PowerShell also takes the typographic quotes as quotes.
        private static readonly char[] PowerShellSignificant = " \t()[]{}<>\"'`!#$&;|*?@%^+=~,\u2018\u2019\u201A\u201B\u201C\u201D\u201E".ToCharArray();

        // The characters cmd /? names as needing quotes, and % that would expand outside them.
        private static readonly char[] CmdSignificant = " \t&()[]{}^=;!'+,`~%".ToCharArray();

        /// <summary>agterm's ShellEscape.path: a backslash before every shell-significant character.</summary>
        public static string ForPosix(string path)
        {
            string result = path;
            foreach (char character in PosixSignificant)
            {
                result = result.Replace(character.ToString(), "\\" + character);
            }

            return result;
        }

        /// <summary>agterm's ShellEscape.path with PowerShell's escape character: a backtick before every significant character.</summary>
        public static string ForPowerShell(string path)
        {
            StringBuilder result = new StringBuilder(path.Length);
            foreach (char character in path)
            {
                if (Array.IndexOf(PowerShellSignificant, character) >= 0)
                {
                    result.Append('`');
                }

                result.Append(character);
            }

            return result.ToString();
        }

        /// <summary>In double quotes when cmd would split or interpret the path otherwise.</summary>
        public static string ForCmd(string path)
        {
            return path.IndexOfAny(CmdSignificant) >= 0 ? "\"" + path + "\"" : path;
        }

        /// <summary>C:\dir becomes /mnt/c/dir; a WSL share path keeps only its Linux part.</summary>
        public static string WslPath(string path)
        {
            if (path.Length > 2 && path[1] == ':')
            {
                return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            }

            if (path.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = path.TrimStart('\\').Split('\\');
                return "/" + string.Join("/", parts.Skip(2));
            }

            return path;
        }
    }
}
