using System.Text.Json;
using System.Text.Json.Serialization;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's LiveReset: selection, next-launch narrowing and reporting for the Reset Live Sessions action and live.reset.
    /// Host-free: the app joins claims to daemons and ends them; this decides which and reports what happened.
    /// </summary>
    public static class LiveReset
    {
        /// <summary>
        /// agterm's only other reason, unsupervised (a leader macOS attributes to the app or orphans), is not selected: a
        /// leader here is always the child of its daemon, and a daemon that answers the listing is alive.
        /// </summary>
        public const string Outdated = "outdated";

        public const int MarkerVersion = 2;

        /// <summary>agterm's supportedVersions: a reset confirmed under the previous marker version still runs after an update.</summary>
        public static readonly IReadOnlySet<int> SupportedVersions = new HashSet<int> { 1, 2 };

        public const string MarkerFilename = "live-reset.json";

        public const string ConsumedFilename = "live-reset.consumed.json";

        /// <summary>A pane confirmed for reset, with the leader seen at confirmation so the next launch tells the same daemon from a replacement.</summary>
        public sealed record Target
        {
            public required string PaneIdentity { get; init; }

            [JsonPropertyName("sessionID")]
            public required string SessionId { get; init; }

            public required string Daemon { get; init; }

            [JsonPropertyName("leaderPID")]
            public required int LeaderPid { get; init; }

            /// <summary>agterm reads a version-1 target, which has no reason, as unsupervised.</summary>
            public string Reason { get; set; } = "unsupervised";
        }

        /// <summary>The confirmed set written at quit and consumed once at the next launch.</summary>
        public sealed record Marker
        {
            public int Version { get; init; } = MarkerVersion;

            /// <summary>Seconds since 1970, as agterm writes its dates.</summary>
            public double CreatedAt { get; init; }

            public required List<Target> Targets { get; init; }
        }

        /// <summary>A pane that claims a daemon: an open pane, one in its undo window, or one of a closed window's saved state.</summary>
        public sealed record Claim(string PaneIdentity, string SessionId);

        /// <summary>agterm's ZmxClaimWalk: the claims, and whether every window that could hold one was read.</summary>
        public sealed record ClaimWalk(IReadOnlyList<Claim> Claims, bool Complete);

        /// <summary>A daemon as the listing shows it: its name (the pane id), its leader and when it started.</summary>
        public sealed record Daemon(string Name, int LeaderPid, DateTime? Created);

        /// <summary>What the dialog offers. An incomplete walk, or one pane claimed twice, forbids the action.</summary>
        public sealed record Selection(IReadOnlyList<Target> Targets, bool InventoryComplete)
        {
            public int SessionCount => Targets.Select(target => target.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            public int OutdatedSessionCount => Targets.Where(target => target.Reason == Outdated).Select(target => target.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        /// <summary>agterm's select: a claimed pane whose daemon started before the cutoff, whatever else; a null cutoff selects none.</summary>
        public static Selection Select(ClaimWalk claims, IReadOnlyList<Daemon> daemons, DateTime? outdatedBefore)
        {
            Dictionary<string, Daemon> byName = ByName(daemons);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool conflicted = false;
            List<Target> targets = new List<Target>();
            foreach (Claim claim in claims.Claims)
            {
                if (!seen.Add(LiveInventory.Identity(claim.PaneIdentity)))
                {
                    conflicted = true;
                    continue;
                }

                if (byName.TryGetValue(LiveInventory.Identity(claim.PaneIdentity), out Daemon? daemon) && daemon.LeaderPid > 0 && IsOutdated(daemon.Created, outdatedBefore))
                {
                    targets.Add(new Target { PaneIdentity = claim.PaneIdentity, SessionId = claim.SessionId, Daemon = daemon.Name, LeaderPid = daemon.LeaderPid, Reason = Outdated });
                }
            }

            return new Selection(targets, claims.Complete && !conflicted);
        }

        public static bool IsOutdated(DateTime? created, DateTime? cutoff)
        {
            return created is DateTime started && cutoff is DateTime before && started < before;
        }

        private static Dictionary<string, Daemon> ByName(IEnumerable<Daemon> daemons)
        {
            Dictionary<string, Daemon> byName = new Dictionary<string, Daemon>(StringComparer.OrdinalIgnoreCase);
            foreach (Daemon daemon in daemons)
            {
                byName.TryAdd(LiveInventory.Identity(daemon.Name), daemon);
            }

            return byName;
        }

        public enum Disposition
        {
            Kill,
            Gone,
            Skipped,
        }

        /// <summary>agterm's Narrowed: the marker re-checked against this launch's claims and listing; it only narrows.</summary>
        public sealed record Narrowed(IReadOnlyDictionary<Target, Disposition> Dispositions, bool InventoryFailed)
        {
            public IReadOnlyList<Target> Kill => Dispositions.Where(pair => pair.Value == Disposition.Kill).Select(pair => pair.Key).OrderBy(target => target.Daemon, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// agterm's narrow: a target is ended when it is still claimed, still listed with the same leader and still started
        /// before the cutoff; one whose daemon is gone restores normally; anything else is skipped. A target of a reason this
        /// build does not select is skipped too.
        /// </summary>
        public static Narrowed Narrow(Marker marker, IReadOnlySet<string>? claimed, IReadOnlyList<Daemon>? daemons, DateTime? outdatedBefore)
        {
            Dictionary<Target, Disposition> dispositions = new Dictionary<Target, Disposition>();
            if (daemons is null || claimed is null)
            {
                foreach (Target target in marker.Targets)
                {
                    dispositions.TryAdd(target, Disposition.Skipped);
                }

                return new Narrowed(dispositions, daemons is null);
            }

            Dictionary<string, Daemon> byName = ByName(daemons);
            HashSet<string> identities = claimed.Select(LiveInventory.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Target target in marker.Targets)
            {
                if (!identities.Contains(LiveInventory.Identity(target.PaneIdentity)))
                {
                    dispositions[target] = Disposition.Skipped;
                }
                else if (!byName.TryGetValue(LiveInventory.Identity(target.Daemon), out Daemon? daemon))
                {
                    dispositions[target] = Disposition.Gone;
                }
                else if (daemon.LeaderPid != target.LeaderPid)
                {
                    dispositions[target] = Disposition.Skipped;
                }
                else
                {
                    dispositions[target] = target.Reason == Outdated && IsOutdated(daemon.Created, outdatedBefore) ? Disposition.Kill : Disposition.Skipped;
                }
            }

            return new Narrowed(dispositions, false);
        }

        /// <summary>
        /// agterm's outcome: survivors are the leaders still alive after the end and the poll; their panes get no saved command.
        /// A session is reset only when every one of its panes was ended or had no daemon.
        /// </summary>
        public static LiveResetOutcome Outcome(Narrowed narrowed, IReadOnlySet<int> survivors, bool inventoryFailed)
        {
            int killed = 0, gone = 0, skipped = 0;
            List<string> unconfirmed = new List<string>();
            HashSet<string> reset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> partial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> unconfirmedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((Target target, Disposition disposition) in narrowed.Dispositions.OrderBy(pair => pair.Key.Daemon, StringComparer.Ordinal))
            {
                switch (disposition)
                {
                    case Disposition.Gone:
                        gone++;
                        reset.Add(target.SessionId);
                        break;
                    case Disposition.Skipped:
                        skipped++;
                        partial.Add(target.SessionId);
                        break;
                    case Disposition.Kill when survivors.Contains(target.LeaderPid):
                        unconfirmed.Add(target.PaneIdentity);
                        partial.Add(target.SessionId);
                        unconfirmedSessions.Add(target.SessionId);
                        break;
                    default:
                        killed++;
                        reset.Add(target.SessionId);
                        break;
                }
            }

            reset.ExceptWith(partial);
            return new LiveResetOutcome
            {
                Panes = new LiveResetPaneCounts { Confirmed = narrowed.Dispositions.Count, Killed = killed, Gone = gone, Skipped = skipped },
                Unconfirmed = unconfirmed,
                Sessions = new LiveResetSessionCounts { Affected = reset.Count + partial.Count, Reset = reset.Count, Partial = partial.Count, Unconfirmed = unconfirmedSessions.Count },
                InventoryFailed = inventoryFailed,
            };
        }

        public static string DialogTitle => Strings.T("Сбросить живые сессии?");

        /// <summary>agterm's dialogText body, in the interface language; agshellctl's reply takes it in English through Strings.InEnglish.</summary>
        public static string DialogBody(int sessions, int outdated)
        {
            string count = sessions == 1 ? Strings.T("1 живая сессия будет сброшена. ") : Strings.T("Будет сброшено живых сессий: {0}. ", sessions);
            string predate = string.Empty;
            if (outdated > 0 && outdated == sessions)
            {
                predate = sessions == 1 ? Strings.T("Она появилась до последнего обновления живых сессий и будет создана заново на текущем. ") : Strings.T("Все они появились до последнего обновления живых сессий и будут созданы заново на текущем. ");
            }
            else if (outdated > 0)
            {
                predate = outdated == 1 ? Strings.T("Одна из них появилась до последнего обновления живых сессий и будет создана заново на текущем. ") : Strings.T("{0} из них появились до последнего обновления живых сессий и будут созданы заново на текущем. ", outdated);
            }

            return count + predate + Strings.T("AgShell сразу закроется и откроется снова с вашими сессиями и раскладкой. Команды, которые шли в этих сессиях, по возможности запустятся снова; остальная работа в них остановится, а разговоры агентов, возможно, придётся продолжить вручную.");
        }

        /// <summary>agterm's notificationText: said only when the reset could not read the list or covered part of the sessions.</summary>
        public static string? NotificationText(LiveResetOutcome outcome)
        {
            if (outcome.InventoryFailed)
            {
                return Strings.T("Живые сессии не сброшены: не удалось прочитать список сессий.");
            }

            if (outcome.Sessions.Partial == 0)
            {
                return null;
            }

            string text = Strings.T("Сброс затронул {0} из {1} живых сессий. Для остальных запустите «Сбросить живые сессии…» ещё раз.", outcome.Sessions.Reset, outcome.Sessions.Affected);
            if (outcome.Sessions.Unconfirmed > 0)
            {
                text += " " + Strings.T("В части сессий ({0}) прежние процессы, возможно, ещё работают; их команды не запущены заново.", outcome.Sessions.Unconfirmed);
            }

            return text;
        }

        /// <summary>agterm's menuVisible: live both as configured and as this launch runs.</summary>
        public static bool MenuVisible(string configured, string active)
        {
            return configured == CommandRestore.Live && active == CommandRestore.Live;
        }
    }

    /// <summary>agterm's LiveResetMarkerStore: the one-shot marker on disk; consume renames before decoding so a crash mid-reset never replays it.</summary>
    public sealed class LiveResetMarkerStore
    {
        private readonly string marker;
        private readonly string consumed;

        public LiveResetMarkerStore(string directory)
        {
            marker = Path.Combine(directory, LiveReset.MarkerFilename);
            consumed = Path.Combine(directory, LiveReset.ConsumedFilename);
        }

        public void Write(LiveReset.Marker value)
        {
            string temporary = marker + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, LiveResetJsonContext.Default.Marker));
            File.Move(temporary, marker, true);
        }

        /// <summary>
        /// Null when no marker exists. Throws InvalidDataException, after removing the file, for one that does not decode as a
        /// supported version; any other exception is the rename failing, and the reset is then not authorized.
        /// </summary>
        public LiveReset.Marker? Consume()
        {
            if (!File.Exists(marker))
            {
                return null;
            }

            if (File.Exists(consumed))
            {
                File.Delete(consumed);
            }

            File.Move(marker, consumed);
            LiveReset.Marker? value = null;
            try
            {
                value = JsonSerializer.Deserialize(File.ReadAllText(consumed), LiveResetJsonContext.Default.Marker);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is NotSupportedException)
            {
            }

            if (value is null || value.Targets is null || !LiveReset.SupportedVersions.Contains(value.Version))
            {
                RemoveConsumed();
                throw new InvalidDataException("the live reset marker is not a supported version");
            }

            return value;
        }

        public void RemoveConsumed()
        {
            try
            {
                File.Delete(consumed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        public void Remove()
        {
            try
            {
                File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            RemoveConsumed();
        }
    }

    /// <summary>
    /// agterm's ZmxBuildRecord, live-build.json: when this state directory first launched the daemon build it names. Live
    /// panes keep their daemons through app updates, so a daemon started before changedAt predates that launch.
    /// </summary>
    public sealed record LiveBuildRecord
    {
        public const string Filename = "live-build.json";

        public required string Id { get; init; }

        /// <summary>Seconds since 1970, whole seconds as agterm floors them.</summary>
        public required double ChangedAt { get; init; }

        public static LiveBuildRecord Advanced(LiveBuildRecord? previous, string bundledId, DateTime now)
        {
            if (previous is not null && previous.Id == bundledId)
            {
                return previous;
            }

            return new LiveBuildRecord { Id = bundledId, ChangedAt = Math.Floor((now.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds) };
        }

        /// <summary>
        /// agterm's launchCutoff: null without a bundled id, which turns the outdated reason off. A record that cannot be saved
        /// still yields this launch's cutoff, and the next launch dates the change again.
        /// </summary>
        public static DateTime? LaunchCutoff(string? bundledId, string directory, DateTime now)
        {
            string? id = bundledId?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            string file = Path.Combine(directory, Filename);
            LiveBuildRecord? previous = Load(file);
            LiveBuildRecord record = Advanced(previous, id, now);
            if (record != previous)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(record, LiveResetJsonContext.Default.LiveBuildRecord));
                    File.Move(file + ".tmp", file, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Warning("live", "live-build.json not saved: " + ex.Message);
                }
            }

            return DateTime.UnixEpoch.AddSeconds(record.ChangedAt);
        }

        private static LiveBuildRecord? Load(string file)
        {
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(file), LiveResetJsonContext.Default.LiveBuildRecord);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
            {
                return null;
            }
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(LiveReset.Marker))]
    [JsonSerializable(typeof(LiveBuildRecord))]
    internal sealed partial class LiveResetJsonContext : JsonSerializerContext
    {
    }
}
