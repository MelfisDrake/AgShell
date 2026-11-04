using System.IO;
using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>agterm's restore modes: with re-run, what each pane ran at quit is typed into its fresh shell on launch.</summary>
    public partial class MainWindow
    {
        private string denylistPath = null!;

        /// <summary>agterm's launchRestore: the app's launch opened this window, so its panes may run what they ran at the last quit.</summary>
        private readonly bool launchRestore;

        private void InitRestore()
        {
            denylistPath = Library.DenylistPath;
            Closing += (sender, args) => CaptureOnClose();
        }

        public bool SaveState()
        {
            return SaveNow();
        }

        /// <summary>agterm's loadStore for a window opened during the run: its sessions come back into the app's events.</summary>
        internal void AnnounceReopened()
        {
            model.AnnounceReopened();
        }

        /// <summary>
        /// agterm's removeWindow and finalizeWindowPanes: the closes still in their undo window become final, then every
        /// shell of the window ends, a live one in the host too, and its sessions are reported closed.
        /// </summary>
        private bool endedForDelete;

        internal void EndForDelete()
        {
            endedForDelete = true;
            foreach (Session session in model.FinalizeAllCloses())
            {
                StopSession(session.Id);
            }

            foreach (Session session in model.AllSessions.ToList())
            {
                StopSession(session.Id);
            }

            model.AnnounceDeleted();
            saveTimer.Stop();
        }

        /// <summary>
        /// agterm's window close: the last window's close is the app's exit, and its panes' programs are noted for the next
        /// launch (in re-run and live; in none forgotten). A window closed while others stay keeps no capture, since a launch
        /// could not tell its file from one open at exit.
        /// </summary>
        /// <summary>agterm's quit-time capture, over this window, before Windows ends the session: then saved.</summary>
        internal bool SaveForQuit()
        {
            if (settings.EffectiveRestoreMode is CommandRestore.Rerun or CommandRestore.Live)
            {
                CaptureRestoreCommands(preservePending: true);
            }
            else
            {
                model.ClearRestoreCommands();
            }

            return SaveNow();
        }

        private void CaptureOnClose()
        {
            // The quit already captured; a second read could find a program that has since ended.
            if (Library.Terminating)
            {
                return;
            }

            if (Library.OpenWindows.Count <= 1 && settings.EffectiveRestoreMode is CommandRestore.Rerun or CommandRestore.Live)
            {
                CaptureRestoreCommands(preservePending: true);
                return;
            }

            model.ClearRestoreCommands();
        }

        /// <summary>
        /// agterm's restore capture, for an exit that never reaches a clean quit (a crash, a power loss): the same capture
        /// a quit makes, now. As in agterm it needs re-run mode.
        /// </summary>
        public (string? Error, int Count) CaptureRestoreNow()
        {
            if (settings.EffectiveRestoreMode != CommandRestore.Rerun)
            {
                return ("restore.capture requires rerun mode; configured restore mode is " + settings.EffectiveRestoreMode, 0);
            }

            int count = CaptureRestoreCommands(preservePending: false);
            SaveNow();
            return (null, count);
        }

        /// <summary>agterm's restore.capture for one window of the app's: the panes captured, and whether the save held.</summary>
        internal (int Count, bool Saved) CaptureRestoreAndSave()
        {
            int count = CaptureRestoreCommands(preservePending: false);
            return (count, SaveNow());
        }

        /// <summary>agterm's restore.clear for one window of the app's: whether the save held.</summary>
        internal bool ClearRestoreAndSave()
        {
            model.ClearRestoreCommands();
            return SaveNow();
        }

        /// <summary>
        /// agterm's captureForegroundCommands: notes the foreground program of every main pane and shown split. A shell
        /// leaves nothing; the denylist is applied when the capture is replayed. At exit a pane that was never started, or
        /// could not be read, keeps what the launch armed for it and did not run (<paramref name="preservePending"/>):
        /// the launch took it out of the file, so writing nothing would lose it. Bounded in time: quitting must not hang.
        /// Returns how many panes it read a program from.
        /// </summary>
        private int CaptureRestoreCommands(bool preservePending)
        {
            List<(Session Session, PaneRole Pane, string? Pending, Task<(string Executable, string CommandLine)?> Capture)> captures = new List<(Session, PaneRole, string?, Task<(string, string)?>)>();
            foreach (Session session in model.AllSessions.Where(session => session.RemoteHost is null))
            {
                foreach (PaneRole pane in new[] { PaneRole.Main, PaneRole.Split })
                {
                    PaneKey key = new PaneKey(session.Id, pane);
                    string? pending = preservePending ? AppModel.PeekPendingCapture(session, pane) : null;
                    if (host.ProcessId(key) is not int shell || (pane == PaneRole.Split && !session.SplitShown))
                    {
                        model.SetRestoreCommand(session.Id, pane, pending);
                        continue;
                    }

                    Task<(string, string)?> capture = session.ProfileId.StartsWith(ShellProfiles.WslIdPrefix, StringComparison.Ordinal)
                        ? Task.Run(() => ProcessTree.WslForeground(session.ProfileId[ShellProfiles.WslIdPrefix.Length..], session.Id, Panes.Name(pane)))
                        : Task.Run(() => ProcessTree.WindowsForeground(shell));
                    captures.Add((session, pane, pending, capture));
                }
            }

            Task.WaitAll(captures.Select(capture => (Task)capture.Capture).ToArray(), TimeSpan.FromSeconds(5));
            int count = 0;
            foreach ((Session session, PaneRole pane, string? pending, Task<(string Executable, string CommandLine)?> capture) in captures)
            {
                string? command = capture.IsCompletedSuccessfully && capture.Result is (string executable, string commandLine)
                    && !string.IsNullOrWhiteSpace(commandLine) && !CommandRestore.IsIdleShellLine(executable, commandLine)
                    ? commandLine
                    : null;
                model.SetRestoreCommand(session.Id, pane, command ?? pending);
                count += command is null ? 0 : 1;
            }

            return count;
        }

        /// <summary>
        /// agterm's LaunchSeedProvider: what a starting main or split pane of this machine runs. <paramref name="take"/>
        /// spends the slots the launch armed; without it the pane is only asked whether it would run something (the pacer).
        /// Live asked for and not available (the fallback) reads neither slot, so they stay for the next launch, and holds
        /// the session's command back as a restored pane outside re-run.
        /// </summary>
        private RestorePlan LaunchSeed(Session session, PaneRole pane, bool take)
        {
            bool fallback = Library.LaunchRestoreMode == CommandRestore.Live && liveDaemons is null;
            bool rerun = Library.LaunchRestoreMode == CommandRestore.Rerun;
            string? capture = fallback ? null : take ? model.TakePendingCapture(session.Id, pane) : AppModel.PeekPendingCapture(session, pane);
            string? pin = fallback ? null : take ? model.TakePendingPin(session.Id, pane) : AppModel.PeekPendingPin(session, pane);
            string? input = rerun && capture is not null && CommandRestore.ShouldRestoreLine(capture, Library.RestoreDenylist) ? capture : null;
            return CommandRestore.Plan(new RestoreInputs(
                session.WasRestored,
                rerun,
                capture is not null,
                input,
                pane == PaneRole.Split ? session.SplitCommand : session.Command,
                pin,
                take && (pane == PaneRole.Split ? session.SplitCommandWait : session.CommandWait)));
        }

        /// <summary>
        /// agterm's LaunchSeed.shouldPace: only a restored pane that would start a program waits for the pacer. A live pane
        /// whose daemon the launch reap saw alive attaches and runs nothing, as does one whose command the reset held back;
        /// another live one runs its captured program unless the denylist stops it, else its durable command. Outside live
        /// the pane paces when its plan runs a command or types one; live asked for and unavailable paces nothing. Another
        /// machine's pane joins through ssh, which is the program it runs.
        /// </summary>
        private bool ShouldPace(Session session, PaneRole pane)
        {
            if (!session.WasRestored)
            {
                return false;
            }

            if (AppModel.IsRemotePane(session, pane))
            {
                return true;
            }

            if (liveDaemons is not null)
            {
                string paneId = model.PaneIdFor(session.Id, pane);
                if (Library.LaunchRunningNames?.Contains(paneId) == true || Library.IsSuppressedLaunch(paneId))
                {
                    return false;
                }

                return AppModel.PeekPendingCapture(session, pane) is string capture
                    ? CommandRestore.ShouldRestoreLine(capture, Library.RestoreDenylist)
                    : (pane == PaneRole.Split ? session.SplitCommand : session.Command) is not null;
            }

            if (Library.LaunchRestoreMode == CommandRestore.Live)
            {
                return false;
            }

            RestorePlan plan = LaunchSeed(session, pane, take: false);
            return plan.Command is not null || plan.Input is not null;
        }

        /// <summary>
        /// agterm's ZmxLaunch.surfaceSeed: what a live pane's daemon runs first if this start creates it, spending the slots
        /// the launch armed. A capture replays unless the denylist stops it (then nothing), with none the pane's command does;
        /// a pane whose shell outlived the reset runs nothing. The pin is spent unread, as agterm's live launch never reads it.
        /// A daemon that is still there keeps its shell and the line goes unused.
        /// </summary>
        private string? LiveReplay(Session session, PaneRole pane)
        {
            string? capture = model.TakePendingCapture(session.Id, pane);
            model.TakePendingPin(session.Id, pane);
            if (Library.TakeSuppressedLaunch(model.PaneIdFor(session.Id, pane)))
            {
                return null;
            }

            return capture is not null
                ? CommandRestore.ShouldRestoreLine(capture, Library.RestoreDenylist) ? capture : null
                : pane == PaneRole.Split ? session.SplitCommand : session.Command;
        }

        /// <summary>Types a line into a freshly started shell once it has shown its prompt and gone quiet, so it lands at the prompt.</summary>
        private async Task TypeAtPromptAsync(PaneKey key, string profileId, string command)
        {
            DateTime started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(100);
                if (host.LastOutputAt(key) is DateTime last && DateTime.UtcNow - last > TimeSpan.FromMilliseconds(200) && EndsWithPrompt(host.OutputTail(key)))
                {
                    break;
                }
            }

            host.Write(key, CommandRestore.ForShell(profileId, command));
            await Task.Delay(Keystrokes.SubmitGap);
            host.Write(key, "\r");
        }

        /// <summary>Whether printed text ends like a shell prompt ($, #, > or %), once escape sequences are taken out.</summary>
        private static bool EndsWithPrompt(string tail)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(tail, @"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[@-Z\\-_])", string.Empty).TrimEnd();
            return text.Length > 0 && text[^1] is '$' or '#' or '>' or '%';
        }

        public (string? Error, ControlRestoreStatus Status) RestoreMode(string? mode)
        {
            if (mode is not null)
            {
                if (!CommandRestore.Modes.Contains(mode))
                {
                    return ("invalid restore mode: " + mode, Library.RestoreStatus());
                }

                string? before = settings.RestoreMode;
                settings.RestoreMode = mode == CommandRestore.None ? null : mode;
                if (!SaveSettings())
                {
                    settings.RestoreMode = before;
                    return ("failed to save restore mode; the previous mode is still in effect", Library.RestoreStatus());
                }
            }

            return (null, Library.RestoreStatus());
        }
    }
}
