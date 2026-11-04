using System.Diagnostics.Tracing;

namespace AgShell.Protocol
{
    /// <summary>
    /// The AgShell provider of the Windows event log, agterm's Logger subsystem: error, warning and notice go to the
    /// AgShell/Operational channel, kept as the macOS log keeps notice and above; info and debug go to AgShell/Debug, off until
    /// an administrator turns it on, as macOS keeps them only when asked. The category is agterm's Logger category.
    /// Its manifest is src/AgShell.EventLog/AgShell.man, checked against this class by a test.
    /// </summary>
    [EventSource(Name = LogEventSource.ProviderName, Guid = LogEventSource.ProviderGuid)]
    internal sealed class LogEventSource : EventSource
    {
        public const string ProviderName = "AgShell";

        public const string ProviderGuid = "8d3f6c1a-52e4-4b7d-9a0e-6c2b1f47d935";

        public static readonly LogEventSource Instance = new LogEventSource();

        public const int ErrorId = 1;

        public const int WarningId = 2;

        public const int NoticeId = 3;

        public const int InfoId = 4;

        public const int DebugId = 5;

        private LogEventSource()
        {
        }

        [Event(ErrorId, Level = EventLevel.Error, Channel = EventChannel.Operational, Message = "[{0}] {1}")]
        public void Error(string category, string message)
        {
            WriteEvent(ErrorId, category, message);
        }

        [Event(WarningId, Level = EventLevel.Warning, Channel = EventChannel.Operational, Message = "[{0}] {1}")]
        public void Warning(string category, string message)
        {
            WriteEvent(WarningId, category, message);
        }

        [Event(NoticeId, Level = EventLevel.Informational, Channel = EventChannel.Operational, Message = "[{0}] {1}")]
        public void Notice(string category, string message)
        {
            WriteEvent(NoticeId, category, message);
        }

        [Event(InfoId, Level = EventLevel.Informational, Channel = EventChannel.Debug, Message = "[{0}] {1}")]
        public void Info(string category, string message)
        {
            WriteEvent(InfoId, category, message);
        }

        [Event(DebugId, Level = EventLevel.Verbose, Channel = EventChannel.Debug, Message = "[{0}] {1}")]
        public void Debug(string category, string message)
        {
            WriteEvent(DebugId, category, message);
        }
    }
}
