using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>
    /// This window as agterm's window commands see it: its entry in the library, its frame on the displays (in the
    /// window's own units, from the display's work area), and the frame states.
    /// </summary>
    public partial class MainWindow
    {
        internal WindowEntry Entry => entry;

        /// <summary>This window's events, which the library folds into the app's one stream.</summary>
        internal ControlEventRing Events => model.Events;

        internal bool IsFullscreen => stateBeforeFullscreen is not null;

        /// <summary>Whether a command's target (a session or workspace id or prefix) is in this window.</summary>
        /// <summary>
        /// agterm's openSession(atDirectory:): a new session in that directory, in the workspace new sessions go to, selected.
        /// False while there is no such workspace yet, so the caller keeps the directory and tries again.
        /// </summary>
        /// <summary>The window knows its shells and has its sessions: a new session can be made in it.</summary>
        internal bool SessionsReady { get; private set; }

        internal event Action? Ready;

        internal bool OpenSessionAt(string directory)
        {
            if (!SessionsReady || model.CurrentWorkspace is not Workspace workspace)
            {
                return false;
            }

            model.AddSession(workspace.Id, DefaultProfile().Id, directory);
            NoteActivity();
            return true;
        }

        internal IEnumerable<Guid> TargetIds(bool workspaces)
        {
            return workspaces ? model.Workspaces.Select(workspace => workspace.Id) : model.AllSessions.Select(session => session.Id);
        }

        internal bool Resolves(string target)
        {
            return ControlTargets.Resolve(target, model.AllSessions.Select(session => session.Id), null).Match == TargetMatch.Resolved
                || ControlTargets.Resolve(target, model.Workspaces.Select(workspace => workspace.Id), null).Match == TargetMatch.Resolved;
        }

        /// <summary>The quick panel's shell: the selected session's shell and directory, as agterm starts it, with no session.</summary>
        internal PtyStartOptions QuickStartOptions(int columns, int rows)
        {
            Session? selected = model.SelectedSessionId is Guid id ? model.FindSession(id) : null;
            ShellProfile profile = selected is null ? DefaultProfile() : ProfileOrDefault(selected.ProfileId);
            // agterm's effectiveCwd: the main pane's directory, whichever pane has the focus.
            return ShellProfiles.CreateStartOptions(profile, selected is null ? null : LaunchDirectory(selected, selected.Cwd), columns, rows, SessionEnvironment(new PaneKey(Guid.Empty, PaneRole.Quick)));
        }

        /// <summary>What the quick panel's page needs to look like the windows: the settings, the theme and the keys.</summary>
        internal (object Settings, object Theme, object Keymap) QuickLook()
        {
            return (SettingsView(), ThemeView(), KeymapView());
        }

        /// <summary>
        /// This window's sessions as another machine sees them in live tree; remote rows are not offered on. The panes'
        /// programs are looked up together off the window's thread, as asking WSL can take up to two seconds a pane.
        /// </summary>
        internal async Task<List<ControlRemoteSession>> RemoteOfferAsync(string window, string windowId)
        {
            List<(ControlRemoteSession Session, PaneRole[] Panes, Task<string?>[] Lines)> offered = model.Workspaces.SelectMany(workspace => workspace.Sessions.Where(session => session.RemoteHost is null).Select(session =>
            {
                PaneRole[] panes = session.HasSplit ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main };
                return (Offer(session, workspace, window, windowId), panes, panes.Select(pane => ForegroundLineAsync(session, pane)).ToArray());
            })).ToList();
            await Task.WhenAll(offered.SelectMany(entry => entry.Lines));
            return offered.Select(entry => entry.Session with
            {
                Panes = entry.Panes.Select((pane, index) => new ControlRemotePane
                {
                    Pane = Panes.Name(pane),
                    Daemon = model.FindSession(Guid.Parse(entry.Session.Id))?.PaneIds.GetValueOrDefault(pane) ?? string.Empty,
                    Foreground = entry.Lines[index].Result is string line ? SplitCommandLine(line) : null,
                }).ToList(),
            }).ToList();
        }

        private static ControlRemoteSession Offer(Session session, Workspace workspace, string window, string windowId)
        {
            return new ControlRemoteSession
            {
                Id = session.Id.Wire(),
                Name = session.DisplayName,
                Workspace = workspace.Name,
                WorkspaceId = workspace.Id.Wire(),
                Window = window,
                WindowId = windowId,
                Cwd = session.Cwd,
                Split = session.HasSplit,
                SplitAxis = session.HasSplit ? Panes.AxisName(session.SplitAxis) : null,
                Status = session.Status == AgentStatus.Idle ? null : AgentStatuses.Name(session.Status),
                StatusColor = session.StatusColor,
                StatusShape = session.StatusShape,
                Context = session.Context,
            };
        }

        private static List<string> SplitCommandLine(string line)
        {
            IntPtr argv = CommandLineToArgvW(line, out int count);
            if (argv == IntPtr.Zero)
            {
                return new List<string> { line };
            }

            try
            {
                return Enumerable.Range(0, count).Select(index => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty).ToList();
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>The program a pane runs, as restore captures it; null for a bare shell or one WSL did not tell in two seconds.</summary>
        private Task<string?> ForegroundLineAsync(Session session, PaneRole pane)
        {
            if (host.ProcessId(new PaneKey(session.Id, pane)) is not int shell)
            {
                return Task.FromResult<string?>(null);
            }

            string? distribution = session.ProfileId.StartsWith(ShellProfiles.WslIdPrefix, StringComparison.Ordinal) ? session.ProfileId[ShellProfiles.WslIdPrefix.Length..] : null;
            Guid sessionId = session.Id;
            string name = Panes.Name(pane);
            return Task.Run(async () =>
            {
                try
                {
                    if (distribution is not null)
                    {
                        Task<(string Executable, string CommandLine)?> asked = ProcessTree.WslForeground(distribution, sessionId, name);
                        return await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(2))) == asked ? (await asked)?.CommandLine : null;
                    }

                    return ProcessTree.WindowsForeground(shell)?.CommandLine;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            });
        }

        /// <summary>One process snapshot serves a whole tree: it is read again only after a quarter second.</summary>
        private (DateTime At, IReadOnlyList<(int Id, int Parent, string Name)> Processes)? processSnapshot;

        public (List<string>? Argv, string? Shell) ForegroundOf(Guid sessionId, PaneRole pane)
        {
            // A row of another machine runs its programs there, and a WSL pane's are Linux processes, out of reach without
            // starting wsl.exe, too slow for a tree: as agterm's unreadable foreground, nothing is reported.
            if (model.FindSession(sessionId) is not Session session || session.RemoteHost is not null
                || session.ProfileId.StartsWith(ShellProfiles.WslIdPrefix, StringComparison.Ordinal)
                || host.ProcessId(new PaneKey(sessionId, pane)) is not int shell || shell <= 0)
            {
                return (null, null);
            }

            if (processSnapshot is not { } taken || DateTime.UtcNow - taken.At > TimeSpan.FromMilliseconds(250))
            {
                processSnapshot = taken = (DateTime.UtcNow, ProcessTree.Processes());
            }

            return ProcessTree.TreeForeground(shell, taken.Processes);
        }

        internal void HoldPresentationStream(string token)
        {
            model.HoldPresentationStream(token);
        }

        internal bool HasRemotePageToken(string? token)
        {
            return model.HasRemotePageToken(token);
        }

        /// <summary>
        /// Claims a handed-over page's channel with its token and makes the connection that page's channel: its end ends the
        /// page, and the page ending shuts it. Null when the token claims nothing.
        /// </summary>
        internal PageServeStream? ClaimPageChannel(string token, Stream connection)
        {
            if (model.ClaimRemotePage(token, DateTimeOffset.UtcNow) is not RemotePage page)
            {
                return null;
            }

            PageServeStream stream = new PageServeStream(connection, page.Spec.Source, page.Spec.Root, () => Dispatcher.BeginInvoke(() => model.EndClaimedRemotePage(page.Id)),
                request => Dispatcher.InvokeAsync(() => AnswerPageBridgeAsync(page.Id, request)).Task.Unwrap());
            pageChannels[page.Id] = stream;
            return stream;
        }

        /// <summary>
        /// agterm's relay: a bridge request of a page handed over, run here where the page sits now, as a local page's
        /// request runs; the answer goes back as the response's JSON.
        /// </summary>
        private async Task<string> AnswerPageBridgeAsync(string pageId, string requestJson)
        {
            if (model.OpenRemotePage(pageId) is not RemotePage page)
            {
                throw new PageBridgeException("page closed");
            }

            ControlRequest sent;
            try
            {
                sent = ControlJson.DeserializeRequest(requestJson);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new PageBridgeException("invalid request: " + ex.Message);
            }

            (ControlRequest? request, string? refusal) = PageBridge.Build(sent, page.SessionId, page.Slot, entry.Id.Wire());
            ControlResponse response = request is null ? ControlResponse.Failure(refusal!) : await Library.DispatchAsync(request);
            return ControlJson.Serialize(response);
        }

        /// <summary>agterm's pendingModalError: the pick or question this window shows, which blocks other control actions.</summary>
        internal string? PendingModalError => model.PendingModalError;

        internal bool HasOverlayJob(string? job)
        {
            return job is not null && model.HasOverlayJob(job);
        }

        /// <summary>
        /// agterm's claimOverlayJob: the one claim the job takes, before its launch deadline, makes the connection the job's.
        /// Null when it claims nothing. The launch context is the one fixed when the overlay opened, or built now.
        /// </summary>
        internal OverlayJobStream? ClaimOverlayJob(string id, Stream connection)
        {
            if (model.OverlayJobById(id) is not OverlayJob job || (job.Launch is null && model.FindSession(job.SessionId) is null))
            {
                return null;
            }

            ControlOverlayJob launch = job.Launch ?? OverlayLaunch(job.SessionId, job.Slot, job.Command, job.Cwd);
            return model.ClaimOverlayJob(job, DateTimeOffset.UtcNow) ? new OverlayJobStream(model, job, launch, connection, Dispatcher) : null;
        }

        internal bool HasWorkspace(Guid workspaceId)
        {
            return model.FindWorkspace(workspaceId) is not null;
        }

        /// <summary>agterm's restore routing: the workspace is in the tree or in a close still inside its undo window.</summary>
        internal bool HoldsWorkspace(Guid workspaceId)
        {
            return model.FindWorkspace(workspaceId) is not null || model.PendingHoldsWorkspace(workspaceId);
        }

        /// <summary>Every session this window holds, in the tree or in a close still inside its undo window.</summary>
        internal IEnumerable<Guid> HeldSessionIds()
        {
            return model.AllSessions.Select(session => session.Id).Concat(model.PendingSessionIds);
        }

        internal RecentClosedRestore ReopenRecentHere(RecentClosedItem item, IReadOnlySet<Guid> occupiedElsewhere)
        {
            return model.ReopenRecent(item, occupiedElsewhere);
        }

        /// <summary>Whether a picker or question id was opened in this window, for pick and ask result and cancel.</summary>
        internal bool KnowsModal(string id)
        {
            return model.PickResult(id) is not null || model.AskResult(id) is not null;
        }

        internal ControlWindowNode Node(string name, bool active)
        {
            List<Displays.Display> displays = Displays.All();
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int display = Displays.Of(handle, displays);
            // agterm's geometry: the frame on screen now — a maximized or full-screen one too; minimized, the one it will come back to.
            Rect frame = WindowState switch
            {
                WindowState.Normal => new Rect(Left, Top, ActualWidth, ActualHeight),
                WindowState.Maximized when Displays.VisibleFrame(handle) is Rect pixels => new Rect(ToUnits(pixels.TopLeft), ToUnits(pixels.BottomRight)),
                _ => RestoreBounds,
            };
            Point origin = ToUnits(displays[display].WorkArea.TopLeft);
            return new ControlWindowNode
            {
                Id = entry.Id.Wire(),
                Name = name,
                Open = true,
                Active = active,
                SidebarVisible = sidebar.Visible,
                AutoFollowMs = AutoFollowMs,
                Geometry = new ControlGeometry
                {
                    X = (int)Math.Round(frame.Left - origin.X),
                    Y = (int)Math.Round(frame.Top - origin.Y),
                    Width = (int)Math.Round(frame.Width),
                    Height = (int)Math.Round(frame.Height),
                    Display = display,
                },
                Fullscreen = IsFullscreen,
                Zoomed = WindowState == WindowState.Maximized && !IsFullscreen,
                Minimized = WindowState == WindowState.Minimized,
            };
        }

        /// <summary>Resizes the frame, held between the window's minimum and its display's work area; returns the size applied.</summary>
        internal (int Width, int Height) ResizeTo(int width, int height)
        {
            LeaveFrameStates();
            Rect area = AreaOf(Displays.Of(new WindowInteropHelper(this).Handle, Displays.All()));
            // Not Math.Clamp: a work area smaller than the 640×400 minimum (a small display at a high scale) would throw.
            Width = Math.Min(Math.Max(width, Math.Max(MinWidth, 200)), area.Width);
            Height = Math.Min(Math.Max(height, Math.Max(MinHeight, 150)), area.Height);
            return ((int)Math.Round(Width), (int)Math.Round(Height));
        }

        /// <summary>Moves the frame's top left to x, y on a display; the origin is held so a strip of the window stays reachable.</summary>
        internal string? MoveTo(int x, int y, int? display)
        {
            List<Displays.Display> displays = Displays.All();
            int target = display ?? Displays.Of(new WindowInteropHelper(this).Handle, displays);
            if (target < 0 || target >= displays.Count)
            {
                return "display " + target + " out of range (have " + displays.Count + ")";
            }

            LeaveFrameStates();
            Rect area = AreaOf(target);
            const double Grab = 80;
            Left = Math.Clamp(area.Left + x, area.Left - ActualWidth + Grab, area.Right - Grab);
            // agterm's clampOrigin: the top may go past the display's top too, as long as a strip of the window stays on it.
            Top = Math.Clamp(area.Top + y, area.Top - ActualHeight + Grab, area.Bottom - Grab);
            return null;
        }

        /// <summary>agterm's window zoom: the normal frame and a maximized one, not full screen.</summary>
        internal void ToggleZoom()
        {
            if (IsFullscreen)
            {
                ToggleFullscreen();
            }

            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        internal void ToggleFullscreenWindow()
        {
            ToggleFullscreen();
        }

        /// <summary>agterm's miniaturize and deminiaturize: back on screen in the state it had, without becoming the active window.</summary>
        internal void SetMinimized(bool minimized)
        {
            if (minimized)
            {
                WindowState = WindowState.Minimized;
            }
            else
            {
                Deminiaturize();
            }
        }

        /// <summary>The name shown after the session in the title once the user named the window.</summary>
        internal void Renamed()
        {
            SyncWindowTitle();
            PostState();
            model.NotifyTreeChanged();
        }

        private void LeaveFrameStates()
        {
            if (IsFullscreen)
            {
                ToggleFullscreen();
            }

            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }
        }

        private Rect AreaOf(int display)
        {
            Rect pixels = Displays.All()[display].WorkArea;
            return new Rect(ToUnits(pixels.TopLeft), ToUnits(pixels.BottomRight));
        }

        private Point ToUnits(Point pixels)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
        }
    }
}
