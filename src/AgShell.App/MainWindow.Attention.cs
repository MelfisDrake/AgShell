using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>Auto-follow to blocked sessions and the status and notification sounds.</summary>
    public partial class MainWindow
    {
        private static readonly string MediaDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

        /// <summary>agterm's mostUsedCommandLimit: the most-run commands that lead the title bar's list.</summary>
        private const int MostUsedCommandLimit = 5;

        private DispatcherTimer autoFollowTimer = null!;
        private DateTime autoFollowArmedAt = DateTime.UtcNow;

        /// <summary>agterm's idle clock: the last input here, none until the first.</summary>
        private DateTime? lastActivity;

        public long? IdleMs => lastActivity is DateTime last ? (long)(DateTime.UtcNow - last).TotalMilliseconds : null;

        public long? AutoFollowMs => settings.AutoFollowTimeout is TimeSpan timeout ? (long)timeout.TotalMilliseconds : null;
        private bool autoFollowArmed = true;
        private bool autoFollowSuppressed;
        private TimeSpan? appliedAutoFollowTimeout;
        private bool appliedAutoFollowStayOnActive;
        private uint lastPageInput;
        private string attentionSignature = string.Empty;
        private string attentionRowsSignature = string.Empty;
        private SoundPlayer? soundPlayer;

        /// <summary>agterm's status sound player is the app's one, so its throttle spans every window.</summary>
        private static readonly SoundThrottle StatusSoundThrottle = new SoundThrottle(TimeSpan.FromMilliseconds(200));

        private void InitAttention()
        {
            autoFollowTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (sender, args) => OnAutoFollowTick(), Dispatcher);
            appliedAutoFollowTimeout = settings.AutoFollowTimeout;
            appliedAutoFollowStayOnActive = settings.AutoFollowStayOnActive == true;
            model.Changed += OnAttentionMaybeChanged;
        }

        /// <summary>agterm's noteUserActivity: a keystroke or a choice the user made stamps the idle clock and starts the grace over.</summary>
        private void NoteActivity()
        {
            lastActivity = DateTime.UtcNow;
            ArmAutoFollow();
        }

        /// <summary>The one place the grace starts; with auto-follow off a pending jump is dropped.</summary>
        private void ArmAutoFollow()
        {
            autoFollowArmed = settings.AutoFollowTimeout is not null;
            autoFollowArmedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// agterm's setAutoFollow: a changed grace or stay-on-active starts the grace over from now, so the next jump is
        /// decided by the new values; turning it off drops a pending one.
        /// </summary>
        private void ApplyAutoFollowSettings()
        {
            TimeSpan? timeout = settings.AutoFollowTimeout;
            bool stayOnActive = settings.AutoFollowStayOnActive == true;
            if (timeout != appliedAutoFollowTimeout || stayOnActive != appliedAutoFollowStayOnActive)
            {
                ArmAutoFollow();
            }

            appliedAutoFollowTimeout = timeout;
            appliedAutoFollowStayOnActive = stayOnActive;
        }

        /// <summary>
        /// As in agterm, a change in the sessions or their statuses while the user is idle gives the full grace again,
        /// without counting as activity. And when this window's attention list changes, every window shows it again.
        /// </summary>
        private void OnAttentionMaybeChanged()
        {
            string signature = string.Join(",", model.AllSessions.Select(session =>
                session.Id + ":" + AgentStatuses.Name(session.Status) + ":" + session.StatusColor + ":" + session.StatusShape + ":" + session.StatusBlink + ":" + Panes.Name(session.StatusPane)));
            if (signature != attentionSignature)
            {
                attentionSignature = signature;
                if (settings.AutoFollowTimeout is not null)
                {
                    ArmAutoFollow();
                }
            }

            string rows = UiActionsEnabled + "|" + string.Join(",", AttentionSessions().Select(entry =>
                entry.Session.Id + ":" + entry.Session.DisplayName + ":" + entry.Workspace + ":" + SwitcherDetail(entry.Session) + ":" + AgentStatuses.Name(entry.Session.Status)
                + ":" + entry.Session.StatusColor + ":" + entry.Session.StatusShape + ":" + entry.Session.StatusChangedAt?.ToUnixTimeMilliseconds()));
            if (rows != attentionRowsSignature)
            {
                attentionRowsSignature = rows;
                Library.RaiseWindowsChanged();
            }
        }

        /// <summary>
        /// agterm's autoFollowFire, once per grace. Suppressed (a palette, a rename, the settings, a title bar list, the
        /// dashboard, a pick or an ask), the jump is dropped until the next activity or change starts the grace again. Its
        /// own jump does not count as activity, so it never keeps pulling.
        /// </summary>
        private void OnAutoFollowTick()
        {
            // As agterm's, typing in a page counts as activity. WebView2 tells this window nothing of the keys a page takes, so
            // while a page has the keyboard any new input the system saw is taken as the user's in it.
            if (nativePages?.HoldsKeyboard() == true && NativePages.LastInputTicks() is uint input && input != lastPageInput)
            {
                lastPageInput = input;
                NoteActivity();
            }

            if (settings.AutoFollowTimeout is not TimeSpan timeout || !autoFollowArmed || DateTime.UtcNow - autoFollowArmedAt < timeout)
            {
                return;
            }

            autoFollowArmed = false;
            if (toolbarPopup?.IsOpen == true || autoFollowSuppressed || model.Dashboard is not null || model.Pick is not null || model.GuiAsk is not null)
            {
                return;
            }

            model.AutoFollow(settings.AutoFollowStayOnActive == true, reveal: IsActive && !model.Zoomed);
        }

        /// <summary>agterm's uiActionsEnabled: no terminal zoom, dashboard, pick or ask covers this window.</summary>
        internal bool UiActionsEnabled => !model.Zoomed && model.Dashboard is null && model.Pick is null && model.GuiAsk is null;

        /// <summary>This window's sessions that need attention, each with its workspace's name.</summary>
        internal IEnumerable<(Session Session, string Workspace)> AttentionSessions()
        {
            return model.AllSessions.Where(session => session.Status != AgentStatus.Idle).Select(session => (session, model.FindWorkspaceOf(session.Id)?.Name ?? string.Empty));
        }

        /// <summary>agterm's switcherDetail: the focused pane's title when it says more than the name, else its directory, led by the machine.</summary>
        private static string SwitcherDetail(Session session)
        {
            string detail = !string.IsNullOrEmpty(session.FocusedTitle) && session.FocusedTitle != session.DisplayName ? session.FocusedTitle : session.FocusedCwd ?? string.Empty;
            return session.RemoteHost is string host ? host + " · " + detail : detail;
        }

        /// <summary>
        /// agterm's attentionAcrossWindows: every open window's sessions needing attention, blocked first, then active, then
        /// completed, the newest status first within each. A row names its window once more than one is open, and can be
        /// chosen only while nothing modal covers that window.
        /// </summary>
        private List<object> AttentionAcrossWindows()
        {
            bool several = Library.OpenWindows.Count > 1;
            return Library.OpenWindows
                .SelectMany(window => window.AttentionSessions().Select(entry => (Window: window, entry.Session, entry.Workspace)))
                .OrderBy(row => AttentionRank(row.Session.Status))
                .ThenByDescending(row => row.Session.StatusChangedAt ?? DateTimeOffset.MinValue)
                .Select(row => (object)new
                {
                    windowId = row.Window.Entry.Id,
                    sessionId = row.Session.Id,
                    name = row.Session.DisplayName,
                    remoteHost = row.Session.RemoteHost,
                    status = AgentStatuses.Name(row.Session.Status),
                    statusColor = Themes.Css(row.Session.StatusColor),
                    statusShape = row.Session.StatusShape,
                    subtitle = (several ? Library.Label(row.Window.Entry) + " · " : string.Empty) + row.Workspace + " · " + SwitcherDetail(row.Session),
                    enabled = row.Window.ToolbarAttentionEnabled,
                })
                .ToList();
        }

        private static int AttentionRank(AgentStatus status)
        {
            return status switch
            {
                AgentStatus.Blocked => 0,
                AgentStatus.Active => 1,
                _ => 2,
            };
        }

        /// <summary>
        /// agterm's selectAttention: a row of the attention list chosen in any window. This window comes to the front, the
        /// choice counts as activity, and the pane that set the status is brought up, the session already selected too.
        /// </summary>
        internal void SelectAttention(Guid sessionId, bool fromToolbar = false)
        {
            if (!UiActionsEnabled || (fromToolbar && !ToolbarAttentionEnabled) || model.FindSession(sessionId) is null)
            {
                return;
            }

            if (Library.Active != this)
            {
                Deminiaturize();
                Activate();
            }

            NoteActivity();
            model.SelectSession(sessionId, reveal: true);
        }

        public void StatusSound(AgentStatus status, string? sound)
        {
            string? name = sound ?? (status == AgentStatus.Blocked ? settings.BlockedStatusSoundName : null);
            PlaySound(name);
        }

        /// <summary>agterm's unknown sound, with the names it can take: default, beep or a sound of C:\Windows\Media.</summary>
        public string? SoundError(string name)
        {
            return name is "default" or "beep" || (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && File.Exists(Path.Combine(MediaDirectory, name + ".wav")))
                ? null
                : "unknown sound: " + name + " (use 'default', 'beep', or one of: " + string.Join(", ", MediaSounds()) + ")";
        }

        /// <summary>
        /// Plays a sound from C:\Windows\Media, or the system alert for default and beep; false when there is no such sound. As
        /// agterm's, the same sound again within 200 ms stays silent and still counts as played.
        /// </summary>
        private bool PlaySound(string? name)
        {
            if (name is "default" or "beep")
            {
                if (StatusSoundThrottle.Allow(name, TimeSpan.FromMilliseconds(Environment.TickCount64)))
                {
                    SystemSounds.Beep.Play();
                }

                return true;
            }

            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            string path = Path.Combine(MediaDirectory, name + ".wav");
            if (!File.Exists(path))
            {
                return false;
            }

            if (!StatusSoundThrottle.Allow(name, TimeSpan.FromMilliseconds(Environment.TickCount64)))
            {
                return true;
            }

            // Kept in a field: Play is asynchronous and plays from the player's buffer.
            soundPlayer = new SoundPlayer(path);
            soundPlayer.Play();
            return true;
        }

        /// <summary>The sounds Windows ships, as the settings list them for the blocked status.</summary>
        private static List<string> MediaSounds()
        {
            return Directory.Exists(MediaDirectory)
                ? Directory.EnumerateFiles(MediaDirectory, "*.wav").Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }

        internal static Uri? ToastSound(string sound)
        {
            return sound switch
            {
                "im" => new Uri("ms-winsoundevent:Notification.IM"),
                "mail" => new Uri("ms-winsoundevent:Notification.Mail"),
                "reminder" => new Uri("ms-winsoundevent:Notification.Reminder"),
                "sms" => new Uri("ms-winsoundevent:Notification.SMS"),
                _ => null,
            };
        }
    }
}
