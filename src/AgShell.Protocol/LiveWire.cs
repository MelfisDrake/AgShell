using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgShell.Protocol
{
    /// <summary>One line of a pane daemon's pipe, in either direction.</summary>
    public sealed record LiveMessage
    {
        /// <summary>
        /// To the daemon: open (attach, or start the shell when missing), attach, detach, write, inject, type, resize, stop,
        /// shutdown, list, screen. From the daemon: opened, output, exited, leads, injected, typed, killed, list, screen.
        /// </summary>
        public required string Op { get; init; }

        public string? Id { get; init; }

        public string? To { get; init; }

        public string? Data { get; init; }

        public string? CommandLine { get; init; }

        public string? Cwd { get; init; }

        public Dictionary<string, string?>? Env { get; init; }

        public int? Cols { get; init; }

        public int? Rows { get; init; }

        public int? Code { get; init; }

        public int? Pid { get; init; }

        public bool? Found { get; init; }

        /// <summary>leads: this client's lead of the pane: leader (its size is the program's), follower, or unowned.</summary>
        public string? Lead { get; init; }

        /// <summary>attach, open: join without taking the lead, as a pane coming back after a drop or after nobody led it.</summary>
        public bool? Follow { get; init; }

        /// <summary>
        /// open, attach, lead: zmx's Manage from a ZMX_MANAGED client: the client leads only by asking, its input while it
        /// follows is dropped, and it is told its lead. A client without it is zmx's stock attach: typing takes the lead.
        /// </summary>
        public bool? Managed { get; init; }

        public int? Request { get; init; }

        /// <summary>screen: scrollback followed by the visible screen, agterm's zmx ScreenMode all; else the visible screen only.</summary>
        public bool? All { get; init; }

        /// <summary>screen reply: advances on every pty read, resize and injected output, so two replies can be told apart.</summary>
        public ulong? Revision { get; init; }

        /// <summary>screen reply: the cursor, zero-based, in the active screen.</summary>
        public int? CursorX { get; init; }

        public int? CursorY { get; init; }

        /// <summary>screen reply: the alternate screen is up.</summary>
        public bool? Alternate { get; init; }

        public List<LivePaneInfo>? Panes { get; init; }

        /// <summary>inject: the sender's process and its ancestors, which find the pane whose shell runs it.</summary>
        public List<int>? Pids { get; init; }
    }

    /// <summary>
    /// A kept shell: its id, its process, how many clients show it (an older host sends none, read as 0), when it started
    /// and the build of the app that started it, as agterm's zmx list gives a daemon's created time, and the shell's working
    /// directory in OSC 7's form, as zmx list's cwd.
    /// </summary>
    public sealed record LivePaneInfo(string Id, int Pid, bool Attached, int Clients = 0, DateTime? Created = null, string? Build = null, string? Cwd = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(LiveMessage))]
    internal sealed partial class LiveJsonContext : JsonSerializerContext
    {
    }

    /// <summary>The live pipe's lines: one JSON object each, in camel case, without nulls.</summary>
    public static class LiveJson
    {
        public static string Serialize(LiveMessage message) => JsonSerializer.Serialize(message, LiveJsonContext.Default.LiveMessage);

        /// <summary>The message on a line; throws JsonException for a line that is not one.</summary>
        public static LiveMessage? Parse(string line) => JsonSerializer.Deserialize(line, LiveJsonContext.Default.LiveMessage);
    }
}
