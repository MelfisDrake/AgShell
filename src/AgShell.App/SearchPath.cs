using System.IO;
using System.Security;
using AgShell.Core;
using Microsoft.Win32;

namespace AgShell.App
{
    /// <summary>The PATH agshell hands every process it starts, read afresh from the registry each time.</summary>
    internal static class SearchPath
    {
        private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

        public static string Current()
        {
            Dictionary<string, string> machine = Read(Registry.LocalMachine, MachineEnvironment);
            Dictionary<string, string> user = Read(Registry.CurrentUser, "Environment");
            string? Lookup(string name)
            {
                if (user.TryGetValue(name, out string? userValue) && !name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    return Environment.ExpandEnvironmentVariables(userValue);
                }

                if (machine.TryGetValue(name, out string? machineValue) && !name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    return Environment.ExpandEnvironmentVariables(machineValue);
                }

                return Environment.GetEnvironmentVariable(name);
            }

            return CommandPath.Widened(AppContext.BaseDirectory, machine.GetValueOrDefault("Path"), user.GetValueOrDefault("Path"), Environment.GetEnvironmentVariable("PATH"), Lookup);
        }

        /// <summary>The variables of one Environment key, unexpanded; none when it cannot be read.</summary>
        private static Dictionary<string, string> Read(RegistryKey root, string subKey)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using RegistryKey? key = root.OpenSubKey(subKey);
                if (key is null)
                {
                    return values;
                }

                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    {
                        values[name] = value;
                    }
                }
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
            {
            }

            return values;
        }
    }
}
