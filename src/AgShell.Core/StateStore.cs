using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgShell.Core
{
    public sealed class StateStore
    {
        // Relaxed escaping keeps non-Latin names readable when the file is opened by hand.
        private static readonly StateJsonContext Json = new StateJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        public StateStore(string path)
        {
            FilePath = path;
        }

        public string FilePath { get; }

        public bool WriteBlocked { get; private set; }

        /// <summary>
        /// Returns null when there is no saved state. Read failures block writes until a successful load; a file that cannot
        /// be parsed is preserved before new state can replace it;
        /// as in agterm, only the version, the workspaces and their ids, names and sessions, and a session's id and
        /// folder are strict, and a bad optional field reads as unset.
        /// </summary>
        public StateSnapshot? Load()
        {
            try
            {
                StateSnapshot? snapshot;
                int? incompatibleVersion = null;
                using (FileStream stream = File.OpenRead(FilePath))
                using (JsonDocument document = JsonDocument.Parse(stream))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("version", out JsonElement version)
                        && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number != StateSnapshot.CurrentVersion)
                    {
                        incompatibleVersion = number;
                        snapshot = null;
                    }
                    else
                    {
                        snapshot = JsonSerializer.Deserialize(document.RootElement, Json.StateSnapshot);
                    }
                }

                if (incompatibleVersion is int unsupported)
                {
                    PreservedFile.MoveAside(FilePath, "version-" + unsupported);
                }

                WriteBlocked = false;
                return snapshot;
            }
            catch (FileNotFoundException)
            {
                WriteBlocked = false;
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                WriteBlocked = false;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteBlocked = true;
                return null;
            }
            catch (JsonException)
            {
                try
                {
                    PreservedFile.MoveAside(FilePath, "corrupt");
                    WriteBlocked = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteBlocked = true;
                }

                return null;
            }
        }

        public void Save(StateSnapshot snapshot)
        {
            if (WriteBlocked)
            {
                throw new IOException("State could not be read; successfully reload it or restart before saving.");
            }

            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = FilePath + ".tmp";
            using (FileStream stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, snapshot, Json.StateSnapshot);
            }

            File.Move(temporaryPath, FilePath, true);
        }
    }

    [JsonSerializable(typeof(StateSnapshot))]
    // The lossy fields read their value through these.
    [JsonSerializable(typeof(Guid?))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(double?))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(List<Guid>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(WindowPlacement))]
    [JsonSerializable(typeof(SidebarSettings))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(AgShell.Protocol.ControlBackground))]
    [JsonSerializable(typeof(Dictionary<string, AgShell.Protocol.ControlBackground>))]
    internal sealed partial class StateJsonContext : JsonSerializerContext
    {
    }
}
