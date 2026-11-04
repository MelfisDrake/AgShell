using System.Diagnostics;
using Microsoft.Win32;

namespace AgShell.App
{
    /// <summary>
    /// agterm's SystemBrowser: opens an address with the default web browser whatever its type, so a local page shows as a
    /// page rather than going to whatever program its file type is set to open with.
    /// </summary>
    internal static class DefaultBrowser
    {
        public static bool Open(Uri url)
        {
            if (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                return true;
            }

            if (Command() is not string command)
            {
                return false;
            }

            // The handler's command line, as "C:\...\browser.exe" --args %1, with the address in place of %1.
            string program;
            string arguments;
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                if (end < 0)
                {
                    return false;
                }

                program = command[1..end];
                arguments = command[(end + 1)..].Trim();
            }
            else
            {
                int space = command.IndexOf(' ');
                program = space < 0 ? command : command[..space];
                arguments = space < 0 ? string.Empty : command[(space + 1)..].Trim();
            }

            string address = "\"" + url.AbsoluteUri + "\"";
            arguments = arguments.Contains("%1", StringComparison.Ordinal) ? arguments.Replace("\"%1\"", address, StringComparison.Ordinal).Replace("%1", address, StringComparison.Ordinal) : (arguments + " " + address).Trim();
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false });
            return true;
        }

        private static string? Command()
        {
            using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId)
            {
                return null;
            }

            using RegistryKey? open = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return open?.GetValue(null) as string;
        }
    }
}
