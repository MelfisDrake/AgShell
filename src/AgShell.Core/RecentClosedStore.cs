using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>recent-closed.json: what Open Recent can bring back, kept across restarts as agterm keeps it.</summary>
    public sealed class RecentClosedStore
    {
        private static readonly RecentJsonContext Json = new RecentJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        public RecentClosedStore(string path)
        {
            FilePath = path;
        }

        public string FilePath { get; }

        /// <summary>
        /// The saved entries; none when the file is missing or unreadable or of another version, which costs only the list,
        /// as agterm's.
        /// </summary>
        public List<RecentClosedItem> Load()
        {
            try
            {
                using FileStream stream = File.OpenRead(FilePath);
                RecentClosedState? state = JsonSerializer.Deserialize(stream, Json.RecentClosedState);
                return state?.Version == RecentClosedState.CurrentVersion ? state.Items.Take(RecentClosedList.Limit).ToList() : new List<RecentClosedItem>();
            }
            catch (Exception)
            {
                return new List<RecentClosedItem>();
            }
        }

        /// <summary>As agterm's: a failed write is logged and swallowed, and false says the file still holds the old list.</summary>
        public bool Save(IReadOnlyList<RecentClosedItem> items)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporaryPath = FilePath + ".tmp";
                using (FileStream stream = File.Create(temporaryPath))
                {
                    JsonSerializer.Serialize(stream, new RecentClosedState { Version = RecentClosedState.CurrentVersion, Items = items.ToList() }, Json.RecentClosedState);
                }

                File.Move(temporaryPath, FilePath, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Notice("RecentClosed", "save recent closed failed: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>recent-closed.json as agterm's RecentClosedState: a version, then the entries newest first.</summary>
    public sealed record RecentClosedState
    {
        public const int CurrentVersion = 1;

        public int Version { get; init; }

        public List<RecentClosedItem> Items { get; init; } = new List<RecentClosedItem>();
    }

    [JsonSerializable(typeof(RecentClosedState))]
    internal sealed partial class RecentJsonContext : JsonSerializerContext
    {
    }
}
