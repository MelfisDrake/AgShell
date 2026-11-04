using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's Logger: what happened, with its category and level, in the system log. On Windows that is the event log under
    /// the AgShell provider (<see cref="LogEventSource"/>), registered by the installer or by Help ▸ Register Event Log…
    /// (ADR 0011). Until it is registered, error, warning and notice go to the Application log under the unregistered source
    /// AgShell, which needs no rights; info and debug, which macOS keeps only when asked, are dropped then.
    /// </summary>
    public static partial class Log
    {
        private const string PublisherKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Publishers\{" + LogEventSource.ProviderGuid + "}";

        private const ushort ErrorType = 0x0001;

        private const ushort WarningType = 0x0002;

        private const ushort InformationType = 0x0004;

        private static readonly object Gate = new object();
        private static bool? registered;
        private static IntPtr fallback;

        public static void Error(string category, string message)
        {
            if (Registered)
            {
                LogEventSource.Instance.Error(category, message);
            }
            else
            {
                Report(ErrorType, LogEventSource.ErrorId, category, message);
            }
        }

        public static void Warning(string category, string message)
        {
            if (Registered)
            {
                LogEventSource.Instance.Warning(category, message);
            }
            else
            {
                Report(WarningType, LogEventSource.WarningId, category, message);
            }
        }

        public static void Notice(string category, string message)
        {
            if (Registered)
            {
                LogEventSource.Instance.Notice(category, message);
            }
            else
            {
                Report(InformationType, LogEventSource.NoticeId, category, message);
            }
        }

        public static void Info(string category, string message)
        {
            LogEventSource.Instance.Info(category, message);
        }

        public static void Debug(string category, string message)
        {
            LogEventSource.Instance.Debug(category, message);
        }

        /// <summary>Whether the AgShell provider is registered with the event log; checked once, again after <see cref="Refresh"/>.</summary>
        public static bool Registered
        {
            get
            {
                lock (Gate)
                {
                    registered ??= IsRegistered();
                    return registered.Value;
                }
            }
        }

        /// <summary>Checks the registration again, after Register Event Log… has run.</summary>
        public static void Refresh()
        {
            lock (Gate)
            {
                registered = null;
            }
        }

        private static bool IsRegistered()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PublisherKey);
                return key is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return false;
            }
        }

        private static void Report(ushort type, int id, string category, string message)
        {
            lock (Gate)
            {
                if (fallback == IntPtr.Zero)
                {
                    fallback = RegisterEventSource(null, LogEventSource.ProviderName);
                    if (fallback == IntPtr.Zero)
                    {
                        return;
                    }
                }

                ReportEvent(fallback, type, 0, (uint)id, IntPtr.Zero, 1, 0, new[] { "[" + category + "] " + message }, IntPtr.Zero);
            }
        }

        [LibraryImport("advapi32.dll", EntryPoint = "RegisterEventSourceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr RegisterEventSource(string? server, string source);

        [LibraryImport("advapi32.dll", EntryPoint = "ReportEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReportEvent(IntPtr eventLog, ushort type, ushort category, uint eventId, IntPtr userSid, ushort strings, uint dataSize, string[] texts, IntPtr data);
    }
}
