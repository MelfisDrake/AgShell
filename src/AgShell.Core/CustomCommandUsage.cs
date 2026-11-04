using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's CustomCommandUsage: run counts of keymap.conf commands, keyed by name, behind the most-used group of the
    /// title bar's custom commands list.
    /// </summary>
    public sealed class CustomCommandUsage
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();

        public void Record(string name)
        {
            Counts[name] = Counts.GetValueOrDefault(name) + 1;
        }

        /// <summary>
        /// The most-run of <paramref name="names"/>, at most <paramref name="limit"/>, by count with ties in their order. A
        /// command never run is left out, and a count whose command is gone takes no slot.
        /// </summary>
        public List<string> MostUsed(IReadOnlyList<string> names, int limit)
        {
            return names
                .Select((name, index) => (name, index, count: Counts.GetValueOrDefault(name)))
                .Where(entry => entry.count > 0)
                .OrderByDescending(entry => entry.count).ThenBy(entry => entry.index)
                .Take(Math.Max(0, limit))
                .Select(entry => entry.name)
                .ToList();
        }
    }

    /// <summary>custom-command-usage.json: read tolerantly, a missing, broken or other-version file counting as empty.</summary>
    public sealed class CustomCommandUsageStore
    {
        private static readonly UsageJsonContext Json = new UsageJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        public CustomCommandUsageStore(string path)
        {
            FilePath = path;
        }

        public string FilePath { get; }

        public CustomCommandUsage Load()
        {
            try
            {
                using FileStream stream = File.OpenRead(FilePath);
                CustomCommandUsage? usage = JsonSerializer.Deserialize(stream, Json.CustomCommandUsage);
                return usage?.Version == CustomCommandUsage.CurrentVersion && usage.Counts is not null ? usage : new CustomCommandUsage();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return new CustomCommandUsage();
            }
        }

        /// <summary>A failed save costs only the counts.</summary>
        public void Save(CustomCommandUsage usage)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporaryPath = FilePath + ".tmp";
                using (FileStream stream = File.Create(temporaryPath))
                {
                    JsonSerializer.Serialize(stream, usage, Json.CustomCommandUsage);
                }

                File.Move(temporaryPath, FilePath, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notice("CustomCommandUsage", "save custom command usage failed: " + ex.Message);
            }
        }
    }

    [JsonSerializable(typeof(CustomCommandUsage))]
    internal sealed partial class UsageJsonContext : JsonSerializerContext
    {
    }
}
