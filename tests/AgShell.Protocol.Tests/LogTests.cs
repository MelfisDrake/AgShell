using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace AgShell.Protocol.Tests
{
    public class LogTests
    {
        [Fact]
        public void TheManifestIsTheEventSources()
        {
            string manifest = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AgShell.EventLog", "AgShell.man"));

            Assert.Equal(Normalize(EventSource.GenerateManifest(typeof(LogEventSource), "AgShell.EventLog.dll")!), Normalize(manifest));
        }

        [Fact]
        public void EachLevelGoesToAgtermsChannelWithItsCategory()
        {
            using Listener listener = new Listener();

            Log.Debug("ZmxClient", "debug text");
            Log.Info("ZmxClient", "info text");
            LogEventSource.Instance.Notice("ControlServer", "notice text");
            LogEventSource.Instance.Warning("ControlServer", "warning text");
            LogEventSource.Instance.Error("ControlServer", "error text");

            Assert.Equal(
                new[]
                {
                    "Verbose Debug ZmxClient debug text",
                    "Informational Debug ZmxClient info text",
                    "Informational Operational ControlServer notice text",
                    "Warning Operational ControlServer warning text",
                    "Error Operational ControlServer error text",
                },
                listener.Events.ToArray());
        }

        private sealed class Listener : EventListener
        {
            public ConcurrentQueue<string> Events { get; } = new ConcurrentQueue<string>();

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == LogEventSource.ProviderName)
                {
                    EnableEvents(eventSource, EventLevel.Verbose);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventSource.Name == LogEventSource.ProviderName && eventData.EventId > 0)
                {
                    Events.Enqueue(eventData.Level + " " + eventData.Channel + " " + eventData.Payload![0] + " " + eventData.Payload[1]);
                }
            }
        }

        private static string Normalize(string manifest)
        {
            return string.Join("\n", manifest.Replace("\r", string.Empty).Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0));
        }

        private static string RepositoryRoot()
        {
            string directory = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(directory, "agshell.slnx")))
            {
                directory = Path.GetDirectoryName(directory)!;
            }

            return directory;
        }
    }
}
