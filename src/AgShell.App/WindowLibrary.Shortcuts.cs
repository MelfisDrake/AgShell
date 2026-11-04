using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace AgShell.App
{
    internal sealed partial class WindowLibrary
    {
        private JsonElement shortcutSnapshot = JsonSerializer.SerializeToElement(new
        {
            pending = Array.Empty<object>(), repeatPrefix = (object?)null,
            heldRepeatKey = (string?)null, consumed = Array.Empty<string>(), menuOwned = Array.Empty<string>(), deadline = (double?)null,
        });
        private readonly Dictionary<string, long> shortcutSequences = new Dictionary<string, long>();
        private string shortcutOrigin = string.Empty;
        private long shortcutSequence;
        private long shortcutRevision;

        internal void ReceiveShortcuts(JsonElement message, bool sourceActive)
        {
            string origin = message.GetProperty("origin").GetString() ?? string.Empty;
            long sequence = message.GetProperty("sequence").GetInt64();
            string? reason = message.GetProperty("reason").GetString();
            if (origin.Length == 0 || sequence <= shortcutSequences.GetValueOrDefault(origin) ||
                !CanPublishShortcut(shortcutOrigin, origin, sourceActive, reason))
            {
                BroadcastShortcuts();
                return;
            }

            shortcutSequences[origin] = sequence;
            shortcutOrigin = origin;
            shortcutSequence = sequence;
            shortcutRevision++;
            shortcutSnapshot = message.GetProperty("snapshot").Clone();
            BroadcastShortcuts();
        }

        internal void ResetShortcuts()
        {
            shortcutSnapshot = JsonSerializer.SerializeToElement(new
            {
                pending = Array.Empty<object>(), repeatPrefix = (object?)null,
                heldRepeatKey = (string?)null, consumed = shortcutSnapshot.GetProperty("consumed"), menuOwned = shortcutSnapshot.GetProperty("menuOwned"), deadline = (double?)null,
            });
            shortcutOrigin = string.Empty;
            shortcutSequence = 0;
            shortcutRevision++;
            BroadcastShortcuts();
        }

        internal static bool CanPublishShortcut(string owner, string origin, bool sourceActive, string? reason)
        {
            return (sourceActive || owner == origin) && (reason != "timer" || owner.Length == 0 || owner == origin);
        }

        internal void BroadcastShortcuts(string[]? released = null)
        {
            object message = new { type = "shortcutState", origin = shortcutOrigin, sequence = shortcutSequence, revision = shortcutRevision, snapshot = shortcutSnapshot, released };
            foreach (MainWindow window in open) window.PostShortcuts(message);
            Quick?.PostShortcuts(message);
            SettingsWindow?.PostShortcuts(message);
        }

        internal void ReconcileShortcutOwnership()
        {
            JsonElement reconciled = ReconcileShortcutOwnership(shortcutSnapshot, GlobalHotkey.IsKeyDown);
            if (reconciled.GetRawText() == shortcutSnapshot.GetRawText()) return;
            string[] released = shortcutSnapshot.GetProperty("consumed").EnumerateArray().Select(key => key.GetString()!)
                .Except(reconciled.GetProperty("consumed").EnumerateArray().Select(key => key.GetString()!)).ToArray();
            shortcutSnapshot = reconciled;
            shortcutOrigin = string.Empty;
            shortcutSequence = 0;
            shortcutRevision++;
            BroadcastShortcuts(released);
        }

        internal static JsonElement ReconcileShortcutOwnership(JsonElement snapshot, Func<string, bool> isDown)
        {
            string[] consumed = snapshot.GetProperty("consumed").EnumerateArray().Select(key => key.GetString()!).Where(isDown).ToArray();
            string[] menuOwned = snapshot.GetProperty("menuOwned").EnumerateArray().Select(key => key.GetString()!).Where(consumed.Contains).ToArray();
            string? held = snapshot.GetProperty("heldRepeatKey").GetString();
            bool released = held is not null && !consumed.Contains(held);
            if (!released && consumed.Length == snapshot.GetProperty("consumed").GetArrayLength()
                && menuOwned.Length == snapshot.GetProperty("menuOwned").GetArrayLength()) return snapshot.Clone();
            double? deadline = snapshot.GetProperty("deadline").ValueKind == JsonValueKind.Number ? snapshot.GetProperty("deadline").GetDouble() : null;
            return JsonSerializer.SerializeToElement(new
            {
                pending = snapshot.GetProperty("pending"), repeatPrefix = snapshot.GetProperty("repeatPrefix"),
                heldRepeatKey = released ? null : held, consumed, menuOwned,
                deadline = released && snapshot.GetProperty("repeatPrefix").ValueKind == JsonValueKind.Array
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500 : deadline,
            });
        }

        internal void ShortcutWindowResigned()
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Application.Current.Windows.OfType<Window>().Any(window => window.IsActive) ||
                    shortcutSnapshot.GetProperty("repeatPrefix").ValueKind != JsonValueKind.Array) return;
                shortcutSnapshot = JsonSerializer.SerializeToElement(new
                {
                    pending = shortcutSnapshot.GetProperty("pending"), repeatPrefix = (object?)null,
                    heldRepeatKey = (string?)null, consumed = shortcutSnapshot.GetProperty("consumed"), menuOwned = shortcutSnapshot.GetProperty("menuOwned"), deadline = (double?)null,
                });
                shortcutOrigin = string.Empty;
                shortcutSequence = 0;
                shortcutRevision++;
                BroadcastShortcuts();
            }, DispatcherPriority.Background);
        }
    }
}
