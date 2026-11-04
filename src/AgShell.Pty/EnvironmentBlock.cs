using System.Collections;
using System.Text;

namespace AgShell.Pty
{
    internal static class EnvironmentBlock
    {
        public static char[]? Build(IReadOnlyDictionary<string, string?>? overrides)
        {
            if (overrides is null || overrides.Count == 0)
            {
                return null;
            }

            SortedDictionary<string, string> variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            }

            foreach (KeyValuePair<string, string?> pair in overrides)
            {
                if (pair.Value is null)
                {
                    variables.Remove(pair.Key);
                }
                else
                {
                    variables[pair.Key] = pair.Value;
                }
            }

            StringBuilder block = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in variables)
            {
                block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }

            block.Append('\0');
            return block.ToString().ToCharArray();
        }
    }
}
