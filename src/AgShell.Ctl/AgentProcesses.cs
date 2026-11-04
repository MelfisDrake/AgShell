using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgShell.Ctl
{
    /// <summary>
    /// Tells the pane's own agent from a worker agent it spawned (a headless <c>claude -p</c> from a tool call).
    /// The worker inherits the pane's AGSHELL_* variables, so its hooks would repaint the spawner's row; one agent
    /// between the hook and the pane means "mine", a second one above it means "a worker".
    /// Fails open: an unreadable chain reports, since a missed guard only repaints a row while a false silence hides a status.
    /// </summary>
    internal static partial class AgentProcesses
    {
        private static readonly HashSet<string> Agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "claude", "codex", "kimi", "opencode", "pi", "gemini", "cursor-agent", "aider", "crush", "goose",
        };

        private static readonly HashSet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node", "bun", "deno", "python3", "python" };

        /// <summary>
        /// The agent names, and as agterm's AGTERM_AGENT_RE, AGSHELL_AGENT_RE extends them; an expression that does not compile
        /// matches nothing, so the walk stays fail-open.
        /// </summary>
        internal static bool IsAgent(string name)
        {
            if (Agents.Contains(name))
            {
                return true;
            }

            if (Environment.GetEnvironmentVariable("AGSHELL_AGENT_RE") is not { Length: > 0 } pattern)
            {
                return false;
            }

            try
            {
                return Regex.IsMatch(name, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// The agent a host process runs, from its argv[1]: its file name, as agterm's, and else the npm command it was started
        /// as. agterm sees the command's name there, since npm links it to the script on macOS; npm on Windows starts
        /// <c>node "…\node_modules\PACKAGE\FILE"</c> from a .cmd or .ps1 shim, so the name is the package.json bin entry for that file.
        /// </summary>
        private static string? HostedAgent(int pid)
        {
            if (CommandLineOf(pid) is not string commandLine)
            {
                return null;
            }

            string[] argv = SplitArguments(commandLine);
            if (argv.Length < 2 || argv[1].Length == 0)
            {
                return null;
            }

            string script = argv[1];
            string name = Path.GetFileName(script.TrimEnd('/', '\\'));
            if (IsAgent(name))
            {
                return name;
            }

            return NpmCommands(script).FirstOrDefault(IsAgent);
        }

        /// <summary>The bin names of the npm package holding the script that point at it.</summary>
        internal static IEnumerable<string> NpmCommands(string script)
        {
            string file;
            try
            {
                file = Path.GetFullPath(script);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                yield break;
            }

            string? directory = Path.GetDirectoryName(file);
            for (int depth = 0; depth < 4 && directory is not null; depth++, directory = Path.GetDirectoryName(directory))
            {
                string manifest = Path.Combine(directory, "package.json");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                List<string> names = new List<string>();
                try
                {
                    using JsonDocument package = JsonDocument.Parse(File.ReadAllText(manifest));
                    JsonElement root = package.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bin", out JsonElement bin))
                    {
                        yield break;
                    }

                    if (bin.ValueKind == JsonValueKind.String && root.TryGetProperty("name", out JsonElement packageName) && packageName.ValueKind == JsonValueKind.String
                        && SameFile(directory, bin.GetString(), file))
                    {
                        // npm names a lone bin after the package, without its scope.
                        string full = packageName.GetString() ?? string.Empty;
                        names.Add(full[(full.LastIndexOf('/') + 1)..]);
                    }
                    else if (bin.ValueKind == JsonValueKind.Object)
                    {
                        names.AddRange(bin.EnumerateObject().Where(entry => entry.Value.ValueKind == JsonValueKind.String && SameFile(directory, entry.Value.GetString(), file)).Select(entry => entry.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    yield break;
                }

                foreach (string name in names)
                {
                    yield return name;
                }

                yield break;
            }
        }

        private static bool SameFile(string directory, string? relative, string file)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(Path.Combine(directory, relative)), file, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        public static bool IsWorkerAgent()
        {
            int agents = 0;
            string? previousAgent = null;
            int pid = Environment.ProcessId;
            for (int depth = 0; depth < 8; depth++)
            {
                int? parent = ParentOf(pid);
                if (parent is not int parentId || parentId <= 4)
                {
                    return false;
                }

                string name;
                try
                {
                    using Process process = Process.GetProcessById(parentId);
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    return false;
                }

                // As agterm's: a CLI hosted by node, bun, deno or python is told by the script it runs.
                if (Hosts.Contains(name) && HostedAgent(parentId) is string hosted)
                {
                    name = hosted;
                }

                if (IsAgent(name))
                {
                    // A launcher that starts the real binary as a child leaves one agent under two pids.
                    if (!string.Equals(name, previousAgent, StringComparison.OrdinalIgnoreCase))
                    {
                        agents++;
                    }

                    previousAgent = name;
                }
                else
                {
                    previousAgent = null;
                }

                if (agents > 1)
                {
                    return true;
                }

                if (string.Equals(name, "agshell", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                pid = parentId;
            }

            return false;
        }

        private static int? ParentOf(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                int status = NtQueryInformationProcess(handle, 0, out ProcessBasicInformation information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
                return status == 0 ? (int)information.InheritedFromUniqueProcessId : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>A process's command line, by ProcessCommandLineInformation; null when it cannot be read.</summary>
        private static string? CommandLineOf(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int length);
                if (length <= 0 || length > 1 << 20)
                {
                    return null;
                }

                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    {
                        return null;
                    }

                    // A UNICODE_STRING: its length in bytes, then a pointer to the text, which follows it in the buffer.
                    ushort bytes = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>A command line split as the C runtime reads its argv.</summary>
        internal static string[] SplitArguments(string commandLine)
        {
            IntPtr argv = CommandLineToArgvW(commandLine, out int count);
            if (argv == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                string[] arguments = new string[count];
                for (int index = 0; index < count; index++)
                {
                    arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
                }

                return arguments;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        private const int ProcessQueryLimitedInformation = 0x1000;

        private const int ProcessCommandLineInformation = 60;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, int length, out int returnLength);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr memory);
    }
}
