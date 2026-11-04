using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// Executes control requests against the model. Runs on the thread that owns the model.
    /// Commands, arguments and error texts follow agterm's control API.
    /// </summary>
    public sealed partial class ControlDispatcher
    {
        private readonly AppModel model;
        private readonly IControlHost host;

        public ControlDispatcher(AppModel model, IControlHost host)
        {
            this.model = model;
            this.host = host;
        }

        public async Task<ControlResponse> HandleAsync(ControlRequest request)
        {
            try
            {
                if (HooksRefusal(request) is string hooksRefused)
                {
                    return Fail(hooksRefused);
                }

                return request.Cmd switch
                {
                    "tree" => Ok(new ControlResult { Tree = BuildTree() }),
                    "version" => Ok(new ControlResult { App = ControlAppIdentity.Parse(host.Version), Installed = host.InstalledVersion is string installed ? ControlAppIdentity.Parse(installed) : null }),
                    "workspace.new" => NewWorkspace(request),
                    "workspace.rename" => RenameWorkspace(request),
                    "workspace.delete" => DeleteWorkspace(request),
                    "workspace.select" => WithWorkspace(request, workspace => model.SelectWorkspace(workspace.Id)),
                    "workspace.go" => GoWorkspace(request),
                    "workspace.move" => MoveWorkspace(request),
                    "workspace.collapse" => WithWorkspace(request, workspace => model.SetWorkspaceCollapsed(workspace.Id, true)),
                    "workspace.expand" => WithWorkspace(request, workspace => model.SetWorkspaceCollapsed(workspace.Id, false)),
                    "session.new" => NewSession(request),
                    "session.duplicate" => DuplicateSession(request),
                    "session.close" => CloseSessions(request),
                    "session.select" => WithSession(request, session => model.SelectSession(session.Id)),
                    "session.go" => GoSession(request),
                    "session.rename" => request.Args?.Name is null ? Fail("session.rename requires a name") : WithSession(request, session => model.RenameSession(session.Id, request.Args.Name)),
                    "session.move" => MoveSessions(request),
                    "session.reveal" => RevealSession(request),
                    "session.restart" => await RestartAsync(request),
                    "session.split" => Split(request),
                    "session.split.close" => CloseSplit(request),
                    "session.swap" => Swap(request),
                    "session.focus" => Focus(request),
                    "session.resize" => Resize(request),
                    "session.scratch" => Scratch(request),
                    "surface.zoom" => Zoom(request),
                    "surface.cursor" => await CursorAsync(request),
                    "session.overlay.open" => OpenOverlay(request),
                    "session.overlay.close" => CloseOverlay(request),
                    "session.overlay.result" => OverlayResult(request),
                    "session.overlay.resize" => ResizeOverlay(request),
                    "session.overlay.text" => await OverlayTextAsync(request),
                    "session.overlay.reload" => PageCommand(request, request.Args?.Current == true ? "reload" : "reload-original"),
                    "session.overlay.copy" => await OverlayCopyAsync(request),
                    "session.overlay.navigate" => Navigate(request),
                    "session.overlay.submit" => Submit(request),
                    "session.overlay.hide" => OverlayVisibility(request, true),
                    "session.overlay.show" => OverlayVisibility(request, false),
                    "session.overlay.toggle" => OverlayVisibility(request, null),
                    "session.hud.open" => OpenHud(request, update: false),
                    "session.hud.update" => OpenHud(request, update: true),
                    "session.hud.close" => CloseHud(request),
                    "session.type" => await TypeAsync(request),
                    "session.text" => await ReadTextAsync(request),
                    "session.status" => SetStatus(request),
                    "session.seen" => WithSession(request, session => model.ClearUnseen(session.Id, clearDelivered: false)),
                    "notify" => Notify(request),
                    "events.read" => ReadEvents(request),
                    "live.presentation" => LivePresent(request),
                    "session.lead" => TakeLead(request),
                    "live.answer" => LiveAnswer(request),
                    "session.search" => await SearchAsync(request),
                    // agterm: one terminal's font, the left pane's unless --pane names the right or the scratch (shown or not).
                    "font.inc" => Font(request, 1, "increase_font_size"),
                    "font.dec" => Font(request, -1, "decrease_font_size"),
                    "font.reset" => Font(request, null, "reset_font_size"),
                    "browser.clear" => await ClearBrowserAsync(),
                    "dashboard" => Dashboard(request),
                    "keymap.reload" => ReloadConfig(keymapFile: true),
                    "hooks.reload" => ReloadConfig(keymapFile: false),
                    "keymap.list" => Ok(new ControlResult { Keymap = host.KeymapList() }),
                    "hooks.list" => Ok(new ControlResult { Hooks = host.HooksList() }),
                    "keymap.run" => await RunCustomAsync(request),
                    "recent.list" => RecentRefusal(request) is string listRefused ? Fail(listRefused) : Ok(new ControlResult { Recent = model.RecentClosed.Select(RecentNode).ToList() }),
                    "recent.open" => OpenRecent(request),
                    "recent.clear" => RecentRefusal(request) is string clearRefused ? Fail(clearRefused) : ClearRecent(),
                    "restore.mode" => RestoreMode(request),
                    "live.list" => await LiveListAsync(),
                    "live.prune" => await LivePruneAsync(),
                    "theme.list" => Ok(ThemeResult(withNames: true)),
                    "theme.set" => SetTheme(request),
                    "sidebar" => Sidebar(request),
                    "session.flag" => FlagSessions(request),
                    "sidebar.mode" => SidebarMode(request),
                    "workspace.focus" => FocusWorkspace(request),
                    "workspace.flag" => FlagWorkspace(request),
                    "workspace.filter" => FocusFilter(request),
                    "sidebar.width" => SidebarWidth(request),
                    "sidebar.expand" => FoldWorkspaces(expand: true),
                    "sidebar.collapse" => FoldWorkspaces(expand: false),
                    "sidebar.flagged-layout" => FlaggedLayout(request),
                    "session.copy" => await CopyAsync(request),
                    "session.selectall" => await SelectAllAsync(request),
                    "session.paste" => await PasteAsync(request),
                    "session.restore" => PinRestore(request),
                    "restore.capture" => CaptureRestore(),
                    "restore.clear" => ClearRestore(),
                    "browser.links" => LinkMode(request),
                    "session.context" => SetContext(request),
                    "session.background" => SetBackground(request),
                    "pick.open" => OpenPick(request),
                    "pick.result" => PickResult(request),
                    "pick.cancel" => CancelPick(request),
                    "ask.open" => OpenAsk(request),
                    "ask.result" => AskResult(request),
                    "ask.cancel" => CancelAsk(request),
                    _ => Fail(UnknownCommand(request.Cmd)),
                };
            }
            catch (ControlException ex)
            {
                return Fail(ex.Message);
            }
        }

        public ControlTree BuildTree()
        {
            Guid? currentWorkspace = model.CurrentWorkspace?.Id;
            return new ControlTree
            {
                SidebarVisible = host.SidebarVisible,
                SidebarWidth = host.SidebarWidth,
                ZoomedSurface = model.ZoomTarget is (Guid zoomedSession, PaneRole zoomedPane) ? SurfaceId(zoomedSession, zoomedPane) : null,
                SidebarMode = model.SidebarMode,
                SidebarFlaggedLayout = host.FlaggedLayout(null).Layout,
                LinkOpenMode = host.LinkMode(null).Mode,
                PickPending = model.Pick?.Id,
                AskPending = model.GuiAsk?.Id,
                FocusEnabled = model.FocusEnabled,
                App = ControlAppIdentity.Parse(host.Version),
                IdleMs = host.IdleMs,
                AutoFollowMs = host.AutoFollowMs,
                DashboardFontMode = model.Dashboard is not DashboardState board ? null : board.FontSize is not null ? "fixed" : board.AutoSize ? "auto" : "untouched",
                DashboardFontSize = model.Dashboard is DashboardState shownBoard ? DashboardLayout.AppliedFontSize(shownBoard, model.DashboardBaseFontSize) : null,
                DashboardMembers = model.Dashboard?.Cells.Select(DashboardMember).ToList(),
                DashboardHighlighted = model.Dashboard is DashboardState dashboard && dashboard.Cells.Count > 0 ? DashboardMember(dashboard.Cells[dashboard.Highlight]) : null,
                Workspaces = model.Workspaces.Select(workspace => new ControlWorkspaceNode
                {
                    Id = workspace.Id.Wire(),
                    Name = workspace.Name,
                    Active = workspace.Id == currentWorkspace,
                    Collapsed = workspace.Collapsed ? true : null,
                    Focused = model.IsFocusMember(workspace.Id) ? true : null,
                    Sessions = workspace.Sessions.Select(session => new ControlSessionNode
                    {
                        Id = session.Id.Wire(),
                        RemoteHost = session.RemoteHost,
                        Presentation = session.RemoteHost is null ? null : host.PresentationOf(session.Id),
                        Presenters = model.PresentersOf(session.Id, DateTimeOffset.UtcNow),
                        Flagged = session.Flagged,
                        Name = session.DisplayName,
                        CustomName = session.CustomName,
                        Cwd = session.Cwd ?? string.Empty,
                        Shell = session.ProfileId,
                        Active = session.Id == model.SelectedSessionId,
                        Status = session.Status == AgentStatus.Idle ? null : AgentStatuses.Name(session.Status),
                        // agterm's indicator: the pane only when the write named one.
                        StatusPane = session.Status == AgentStatus.Idle || !session.StatusPaneNamed ? null : Panes.Name(session.StatusPane),
                        Blink = session.StatusBlink ? true : null,
                        StatusChangedAt = session.StatusChangedAt is DateTimeOffset changed ? (changed - DateTimeOffset.UnixEpoch).TotalSeconds : null,
                        Unseen = session.UnseenCount > 0 ? session.UnseenCount : null,
                        Split = session.SplitShown,
                        HasSplit = session.HasSplit ? true : null,
                        SplitAxis = session.HasSplit ? Panes.AxisName(session.SplitAxis) : null,
                        // agterm's: absent until the divider is moved, an even split.
                        SplitRatio = session.HasSplit ? session.SplitRatio : null,
                        SplitCwd = session.HasSplit ? session.SplitCwd : null,
                        Title = session.Title,
                        Context = session.EffectiveContext,
                        Command = session.Command,
                        // agterm's: true for a command that holds, an attached row's ssh among them; else absent.
                        CommandWait = (session.Command is not null && session.CommandWait) || AppModel.IsRemotePane(session, PaneRole.Main) ? true : null,
                        Surfaces = SurfacesOf(session),
                        Background = session.Background,
                        PaneBackgrounds = session.PaneBackgrounds.Count == 0 ? null : session.PaneBackgrounds.ToDictionary(pair => Panes.Name(pair.Key), pair => pair.Value),
                        Ask = model.TerminalAskOf(session.Id) is AskRequest ask
                            ? new ControlAskNode { Id = ask.Id, Pane = ask.Pane is PaneRole askPane ? Panes.Name(askPane) : null, Remote = ask.RemoteOwner is null ? null : true, Replica = ask.Replica ? true : null }
                            : null,
                        RemoteOverlays = model.RemoteOverlaysOf(session.Id),
                        StatusColor = session.StatusColor,
                        StatusShape = session.StatusShape,
                        RestoreCommand = session.RestorePin,
                        SplitRestoreCommand = session.HasSplit ? session.SplitRestorePin : null,
                        SplitFocused = session.HasSplit ? session.SplitFocused : null,
                        FontSize = host.IsRunning(session.Id, PaneRole.Main) ? session.FontSize ?? model.BaseFontSize : null,
                        SplitFontSize = session.HasSplit && host.IsRunning(session.Id, PaneRole.Split) ? session.SplitFontSize ?? model.BaseFontSize : null,
                        ScratchFontSize = host.IsRunning(session.Id, PaneRole.Scratch) ? session.ScratchFontSize ?? model.BaseFontSize : null,
                        Foreground = host.ForegroundOf(session.Id, PaneRole.Main).Argv,
                        ForegroundShell = host.ForegroundOf(session.Id, PaneRole.Main).Shell,
                        SplitForeground = session.HasSplit ? host.ForegroundOf(session.Id, PaneRole.Split).Argv : null,
                        SplitForegroundShell = session.HasSplit ? host.ForegroundOf(session.Id, PaneRole.Split).Shell : null,
                        Realized = host.IsRunning(session.Id, PaneRole.Main),
                        BackedByZmx = session.RemoteHost is not null ? null : host.PaneLead(session.Id, PaneRole.Main) is not null && (!session.HasSplit || host.PaneLead(session.Id, PaneRole.Split) is not null),
                        SplitCommandWait = session.HasSplit && AppModel.IsRemotePane(session, PaneRole.Split) ? true : null,
                        Scratch = session.ScratchShown,
                        // As agterm's, a HUD is not an overlay: it reports itself in hud, so a script never takes it for a program.
                        Overlay = session.Overlay is { IsHud: false },
                        Hud = session.Overlay is { Hud: HudSpec shown } panel ? HudNode(panel, shown) : null,
                        OverlaySizePercent = session.Overlay is { IsHud: false } floating ? floating.SizePercent : null,
                        OverlayWidth = session.Overlay is { IsHud: false, SizePercent: not null } sized ? host.OverlayGeometry(sized.Id)?.Width : null,
                        OverlayHeight = session.Overlay is { IsHud: false, SizePercent: not null } sizedToo ? host.OverlayGeometry(sizedToo.Id)?.Height : null,
                        HtmlOverlays = HtmlOverlaysOf(session),
                        // A page shown on the presenter counts as that machine reports it, as agterm's.
                        HiddenOverlays = AppModel.HideableSlots(session, null).Where(slot => session.Overlays[slot].Hidden)
                            .Concat(model.RemotePagesOf(session.Id).Where(page => page.Reported?.Hidden == true).Select(page => page.Slot))
                            .OrderBy(slot => slot)
                            .Select(slot => slot == PaneRole.Overlay ? "session" : slot == PaneRole.OverlayLeft ? "left" : "right").ToList() is { Count: > 0 } hiddenSlots ? hiddenSlots : null,
                        PaneOverlays = session.Overlays.Keys.Where(slot => slot != PaneRole.Overlay).Select(slot => slot == PaneRole.OverlayRight ? "right" : "left").OrderBy(name => name).ToList() is { Count: > 0 } panes ? panes : null,
                    }).ToList(),
                }).ToList(),
            };
        }

        private ControlResponse NewWorkspace(ControlRequest request)
        {
            Workspace workspace = model.AddWorkspace(request.Args?.Name, request.Args?.Collapsed != true);
            if (request.Args?.Collapsed == true)
            {
                model.SetWorkspaceCollapsed(workspace.Id, true);
            }

            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        private ControlResponse RenameWorkspace(ControlRequest request)
        {
            string name = string.IsNullOrWhiteSpace(request.Args?.Name) ? throw new ControlException("workspace.rename requires a name") : request.Args.Name;
            return WithWorkspace(request, workspace => model.RenameWorkspace(workspace.Id, name));
        }

        private ControlResponse DeleteWorkspace(ControlRequest request)
        {
            Workspace workspace = ResolveWorkspace(request.Target);
            if (!model.CanRemoveWorkspace)
            {
                return Fail("cannot delete last workspace");
            }

            foreach (Session session in workspace.Sessions)
            {
                host.StopSession(session.Id);
            }

            model.RemoveWorkspace(workspace.Id);
            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        private ControlResponse GoWorkspace(ControlRequest request)
        {
            int offset = request.Args?.To switch
            {
                // agterm's WorkspaceNavigation takes previous too.
                "next" => 1,
                "prev" or "previous" => -1,
                _ => throw new ControlException("workspace.go requires --to next|prev"),
            };

            return model.SelectRelativeWorkspace(offset) ? Ok(new ControlResult { Id = model.CurrentWorkspace?.Id.Wire() }) : Fail("no other workspace to navigate to");
        }

        private ControlResponse MoveWorkspace(ControlRequest request)
        {
            Workspace workspace = ResolveWorkspace(request.Target);
            int index = model.Workspaces.ToList().IndexOf(workspace);
            int destination = Reorder(request.Args?.To, index, model.Workspaces.Count, "workspace.move");
            model.MoveWorkspace(workspace.Id, destination);
            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        private ControlResponse NewSession(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (args.After is not null && args.Before is not null)
            {
                return Fail("use either --after or --before, not both");
            }

            if ((args.After is not null || args.Before is not null) && (args.Workspace is not null || args.WorkspaceName is not null))
            {
                return Fail("session.new takes --after/--before or a workspace, not both");
            }

            if (args.Workspace is not null && args.WorkspaceName is not null)
            {
                return Fail("use either --workspace or --workspace-name, not both");
            }

            if (args.CreateWorkspace == true && args.WorkspaceName is null)
            {
                return Fail("--create-workspace requires --workspace-name");
            }

            if (args.Wait == true && args.Command is null)
            {
                return Fail("--wait requires --command");
            }

            string shell = args.Shell ?? host.DefaultShellId;
            if (!host.ShellIds.Contains(shell))
            {
                return Fail("no such shell: " + shell + " (available: " + string.Join(", ", host.ShellIds) + ")");
            }

            Workspace workspace;
            int? index = null;
            if (args.After is not null || args.Before is not null)
            {
                Session anchor = ResolveSession(args.After ?? args.Before);
                workspace = model.FindWorkspaceOf(anchor.Id)!;
                index = workspace.Sessions.ToList().IndexOf(anchor) + (args.After is not null ? 1 : 0);
            }
            else if (args.WorkspaceName is not null)
            {
                // agterm sanitizes the needle like the stored names, or a name with a control character misses its own workspace.
                string name = TerminalText.Sanitized(args.WorkspaceName).Trim();
                if (name.Length == 0)
                {
                    return Fail("workspace name must not be blank");
                }

                workspace = model.Workspaces.FirstOrDefault(candidate => candidate.Name == name)
                    ?? (args.CreateWorkspace == true ? model.AddWorkspace(name, args.NoSelect != true) : throw new ControlException("no workspace named \"" + name + "\" (pass --create-workspace to add it)"));
            }
            else
            {
                workspace = args.Workspace is not null ? ResolveWorkspace(args.Workspace) : model.CurrentWorkspace ?? model.AddWorkspace();
            }

            string? command = args.Command?.Trim();
            if (command is not null && (command.Length == 0 || command.Any(char.IsControl)))
            {
                return Fail("--command must be one line of text");
            }

            Session session = model.AddSession(workspace.Id, shell, string.IsNullOrWhiteSpace(args.Cwd) ? null : args.Cwd, index, args.Name, args.NoSelect != true);
            if (command is not null)
            {
                model.SetCommand(session.Id, command, args.Wait == true);
            }

            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse DuplicateSession(ControlRequest request)
        {
            Session source = ResolveSession(request.Target);
            Session copy = model.DuplicateSession(source.Id, host.DefaultShellId);
            return Ok(new ControlResult { Id = copy.Id.Wire() });
        }

        private ControlResponse CloseSessions(ControlRequest request)
        {
            if (request.Args?.Targets is { Count: 0 })
            {
                return Fail("session.close requires at least one --target");
            }

            List<Session> sessions = ResolveSessions(request);
            // agterm: one target closes at once and answers its id; a batch is one grouped close the undo grace covers.
            if ((request.Args?.Targets?.Count ?? 0) <= 1)
            {
                host.StopSession(sessions[0].Id);
                model.RemoveSession(sessions[0].Id);
                return Ok(new ControlResult { Id = sessions[0].Id.Wire() });
            }

            return Ok(new ControlResult { Affected = host.CloseSessionsBatch(sessions.Select(session => session.Id).ToList()) });
        }

        private static bool? OnOffToggle(string? mode, string noun)
        {
            return mode switch
            {
                "on" => true,
                "off" => false,
                "toggle" or null => null,
                _ => throw new ControlException("invalid " + noun + " mode: " + mode),
            };
        }

        private ControlResponse FlagSessions(ControlRequest request)
        {
            if (request.Args?.Mode == "clear")
            {
                model.ClearFlags();
                return Ok();
            }

            List<Session> sessions = ResolveSessions(request);
            model.SetFlag(sessions.Select(session => session.Id).ToList(), OnOffToggle(request.Args?.Mode, "flag"));
            return Ok(sessions.Count == 1 ? new ControlResult { Id = sessions[0].Id.Wire() } : new ControlResult { Affected = sessions.Count });
        }

        /// <summary>
        /// agterm's workspace.flag: every session of one workspace as one value, those the flagged view hides included; toggle
        /// unflags only when all are flagged, otherwise flags them all.
        /// </summary>
        private ControlResponse FlagWorkspace(ControlRequest request)
        {
            bool? flagged = request.Args?.Mode switch
            {
                "on" => true,
                "off" => false,
                "toggle" or null => null,
                string other => throw new ControlException("invalid workspace flag mode: " + other),
            };
            Workspace workspace = ResolveWorkspace(request.Target);
            if (workspace.Sessions.Count > 0)
            {
                model.SetFlag(workspace.Sessions.Select(session => session.Id).ToList(), flagged);
            }

            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        /// <summary>agterm's sidebar.expand/collapse: a no-op under the flat flagged list, which has no workspace rows.</summary>
        private ControlResponse FoldWorkspaces(bool expand)
        {
            if (model.SidebarMode == AppModel.FlaggedMode && host.FlaggedLayout(null).Layout != "tree")
            {
                return Ok(new ControlResult());
            }

            if (expand)
            {
                model.ExpandAllWorkspaces();
            }
            else
            {
                model.CollapseOtherWorkspaces();
            }

            return Ok(new ControlResult());
        }

        private ControlResponse FlaggedLayout(ControlRequest request)
        {
            (string? error, string layout) = host.FlaggedLayout(request.Args?.Mode ?? "toggle");
            return error is null ? Ok(new ControlResult { Text = layout }) : Fail(error);
        }

        /// <summary>agterm's session.copy: the selection in the session's focused pane, never the system clipboard.</summary>
        /// <summary>
        /// agterm's font: a pane's terminal font, the main pane by default; a pane under a page on screen steps the pages'
        /// zoom instead, as the keys do.
        /// </summary>
        private ControlResponse Font(ControlRequest request, double? delta, string action)
        {
            // agterm's dispatchFont reads the pane before the session.
            if (request.Args?.Pane is string named && !Panes.TryParse(named, out _))
            {
                return Fail("invalid pane: " + named);
            }

            Session session = ResolveSession(request.Target);
            PaneRole pane = ResolvePane(session, request.Args?.Pane, PaneRole.Main);
            if (HtmlHidesTerminal(session, pane))
            {
                host.StepPageZoom(action);
            }
            else
            {
                model.ChangeFontSize(session.Id, delta, pane);
            }

            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>agterm's htmlHidesTerminal: a page on screen over the whole session, or over that pane.</summary>
        private static bool HtmlHidesTerminal(Session session, PaneRole pane)
        {
            bool sessionPage = session.Overlay is { Page: not null, Hidden: false };
            return pane switch
            {
                PaneRole.Scratch => sessionPage && session.ScratchShown,
                _ => sessionPage || session.Overlays.GetValueOrDefault(Panes.OverlayOf(pane)) is { Page: not null, Hidden: false },
            };
        }

        /// <summary>agterm's copySelection: the main pane, the one session.selectall selects.</summary>
        private async Task<ControlResponse> CopyAsync(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            RequireUncovered(session, PaneRole.Main, hereToo: true);
            string? text = await host.TerminalAsync(session.Id, PaneRole.Main, "copy", null);
            return text switch
            {
                null => Fail("session not realized"),
                "" => Fail("no selection"),
                _ => Ok(new ControlResult { Text = text }),
            };
        }

        private async Task<ControlResponse> SelectAllAsync(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            // These act on the terminal itself, so agterm refuses them on a covered pane on the session's own machine too.
            RequireUncovered(session, PaneRole.Main, hereToo: true);
            return await host.TerminalAsync(session.Id, PaneRole.Main, "selectAll", null) is null
                ? Fail("session not realized")
                : Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>agterm's session.paste: the clipboard pasted into a pane as Ctrl+V would, bracketed and not submitted.</summary>
        private async Task<ControlResponse> PasteAsync(ControlRequest request)
        {
            // agterm's dispatcher reads the pane first, in its own words.
            string? named = request.Args?.Pane;
            if (named is not null && !Panes.TryParse(named, out _))
            {
                return Fail("--pane must be left, right, or scratch");
            }

            Session session = ResolveSession(request.Target);
            PaneRole pane = ResolvePane(session, named, PaneRole.Main);
            RequireUncovered(session, pane, hereToo: true);
            // As agterm's paste_from_clipboard: an empty clipboard pastes nothing, and that is no error.
            return await host.TerminalAsync(session.Id, pane, "paste", host.ReadClipboard() ?? string.Empty) is null
                ? Fail("session not realized")
                : Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>
        /// agterm's session.restore: pins what a pane re-runs on every launch (--none for a plain shell) or, with --clear,
        /// goes back to the capture. It acts under re-run; in another mode the pin is kept for later and a note says so.
        /// </summary>
        private ControlResponse PinRestore(ControlRequest request)
        {
            // agterm's dispatchSessionRestore: the mode and the line are checked before the session, and the line is
            // never rewritten; only C0 controls and DEL are refused, as CommandRestore.hasControlCharacter.
            string? command = request.Args?.Command;
            string? mode = request.Args?.Mode;
            switch (mode)
            {
                case "set":
                    if (command is null)
                    {
                        return Fail("session.restore set requires a command");
                    }

                    if (CommandRestore.HasControlCharacter(command))
                    {
                        return Fail("command must not contain control characters");
                    }

                    if (System.Text.Encoding.UTF8.GetByteCount(command) > MaxRestoreCommandBytes)
                    {
                        return Fail("command too long (max " + MaxRestoreCommandBytes + " bytes)");
                    }

                    break;
                case "none":
                    command = string.Empty;
                    break;
                case "clear":
                    command = null;
                    break;
                default:
                    return Fail("invalid restore mode: " + mode + " (set|none|clear)");
            }

            PaneRole pane = PaneRole.Main;
            if (request.Args?.Pane is string name && !Panes.TryParse(name, out pane))
            {
                return Fail("--pane must be left, right, or scratch");
            }

            Session session = ResolveSession(request.Target);
            pane = PaneById(session, request.Args, alwaysFallBack: false) ?? pane;
            if (pane == PaneRole.Scratch)
            {
                return Fail("the scratch terminal is never restored");
            }

            if (pane == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split");
            }

            // agterm's setRestoreCommand: the pin holds only once it is on disk; a failed save puts the previous one back.
            string? before = pane == PaneRole.Split ? session.SplitRestorePin : session.RestorePin;
            model.SetRestorePin(session.Id, pane, command);
            if (before != command && !host.SaveState())
            {
                model.SetRestorePin(session.Id, pane, before);
                return Fail("failed to save the restore override, the previous value is still in effect");
            }

            // agterm's note names the mode this launch restores with, not the one set for the next.
            string launched = host.RestoreMode(null).Status.RequestedAtLaunch;
            string? note = mode != "clear" && launched != CommandRestore.Rerun ? "saved for rerun mode; active restore mode is " + launched : null;
            return Ok(new ControlResult { Id = session.Id.Wire(), Pane = Panes.Name(pane), Text = note });
        }

        /// <summary>
        /// agterm's session.context: one trimmed line of at most 256 UTF-8 bytes, or --clear. A rejected value leaves the
        /// previous one standing; a blank one is an error rather than a second way to clear.
        /// </summary>
        private ControlResponse SetContext(ControlRequest request)
        {
            string? text = request.Args?.Text;
            switch (request.Args?.Mode)
            {
                case "set":
                    if (text is null)
                    {
                        return Fail("session.context set requires text");
                    }

                    if (Session.ContextError(text) is string error)
                    {
                        return Fail(error);
                    }

                    text = text.Trim();
                    break;
                case "clear":
                    if (text is not null)
                    {
                        return Fail("session.context clear takes no text");
                    }

                    break;
                default:
                    return Fail("invalid context mode: " + request.Args?.Mode + " (set|clear)");
            }

            Session session = ResolveSession(request.Target);
            model.SetContext(session.Id, text);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse CaptureRestore()
        {
            (string? error, int count) = host.CaptureRestoreNow();
            return error is null ? Ok(new ControlResult { Count = count, Text = "captured " + count + " pane" + (count == 1 ? string.Empty : "s") }) : Fail(error);
        }

        private ControlResponse ClearRestore()
        {
            model.ClearRestoreCommands();
            return Ok(new ControlResult());
        }

        private ControlResponse LinkMode(ControlRequest request)
        {
            (string? error, string mode) = host.LinkMode(request.Args?.Mode);
            return error is null ? Ok(new ControlResult { Text = mode }) : Fail(error);
        }

        /// <summary>
        /// agterm's --pane-id: a known id names its pane now. An unknown one falls back on an explicit --pane, and is an error
        /// without one, unless the command always falls back (session status, which then means the left pane).
        /// </summary>
        private static PaneRole? PaneById(Session session, ControlArgs? args, bool alwaysFallBack)
        {
            if (args?.PaneId is not { Length: > 0 } id)
            {
                return null;
            }

            if (AppModel.PaneOfId(session, id) is PaneRole pane)
            {
                return pane;
            }

            return args.Pane is not null || alwaysFallBack ? null : throw new ControlException("unknown pane id: " + id);
        }

        /// <summary>
        /// agterm's surfaces: the session's terminals by surface id, as TerminalZoomSurface lists them. The panes, a scratch
        /// that is shown or still running, and the program overlays on screen; a HUD, a page or a hidden overlay is no
        /// terminal to address, and neither covers anything. At most one is active.
        /// </summary>
        private List<ControlSurfaceNode> SurfacesOf(Session session)
        {
            bool coverVisible = session.Overlay is { IsHud: false, Hidden: false };
            bool programVisible = session.Overlay is { IsHud: false, Page: null, Hidden: false };
            bool PaneOverlayVisible(PaneRole pane) => session.Overlays.TryGetValue(Panes.OverlayOf(pane), out SessionOverlay? own) && !own.Hidden;
            bool PaneOverlayIsHtml(PaneRole pane) => session.Overlays.TryGetValue(Panes.OverlayOf(pane), out SessionOverlay? own) && own.Page is not null;
            bool PaneVisible(PaneRole pane) => !coverVisible && !session.ScratchShown && session.RendersPane(pane);
            PaneRole? active = AppModel.ActiveSurface(session);

            // agterm's isAvailable(.primary): not while the split is focused, running, and the main pane has no terminal.
            List<PaneRole> panes = new List<PaneRole>();
            if (host.IsRunning(session.Id, PaneRole.Main) || !session.HasSplit || !session.SplitFocused || !host.IsRunning(session.Id, PaneRole.Split))
            {
                panes.Add(PaneRole.Main);
            }

            if (session.HasSplit)
            {
                panes.Add(PaneRole.Split);
            }

            if (session.ScratchShown || host.IsRunning(session.Id, PaneRole.Scratch))
            {
                panes.Add(PaneRole.Scratch);
            }

            if (programVisible)
            {
                panes.Add(PaneRole.Overlay);
            }

            foreach (PaneRole pane in new[] { PaneRole.Main, PaneRole.Split })
            {
                if (PaneOverlayVisible(pane) && !PaneOverlayIsHtml(pane))
                {
                    panes.Add(Panes.OverlayOf(pane));
                }
            }

            bool Visible(PaneRole pane) => pane switch
            {
                PaneRole.Main or PaneRole.Split => PaneVisible(pane) && !PaneOverlayVisible(pane),
                PaneRole.Scratch => !coverVisible && session.ScratchShown,
                PaneRole.Overlay => programVisible,
                PaneRole.OverlayLeft => PaneVisible(PaneRole.Main),
                _ => PaneVisible(PaneRole.Split),
            };

            return panes.Select(pane => new ControlSurfaceNode
            {
                Id = SurfaceId(session.Id, pane),
                Kind = Panes.Name(pane),
                Active = pane == active,
                Visible = Visible(pane),
                PaneId = session.PaneIds.GetValueOrDefault(pane),
                Lead = pane is PaneRole.Main or PaneRole.Split ? host.PaneLead(session.Id, pane) : null,
                // agterm's backedByZmx per surface: a left or right pane of this machine kept by a live daemon; null for the rest.
                BackedByZmx = pane is PaneRole.Main or PaneRole.Split && session.RemoteHost is null ? host.PaneLead(session.Id, pane) is not null : null,
                Reconnect = AppModel.IsRemotePane(session, pane) ? host.ReconnectOf(session.Id, pane) : null,
            }).ToList();
        }

        private ControlResponse SidebarMode(ControlRequest request)
        {
            string? mode = request.Args?.Mode;
            if (mode is not (null or "toggle" or AppModel.TreeMode or AppModel.FlaggedMode))
            {
                return Fail("invalid sidebar mode: " + mode);
            }

            model.SetSidebarMode(mode == "toggle" ? null : mode);
            return Ok();
        }

        /// <summary>agterm's workspace.focus: on narrows to this workspace, off unmarks it, toggle is the row menu's Focus/Unfocus, add marks it too.</summary>
        private ControlResponse FocusWorkspace(ControlRequest request)
        {
            string mode = request.Args?.Mode ?? "toggle";
            if (mode is not ("on" or "off" or "toggle" or "add"))
            {
                return Fail("invalid focus mode: " + request.Args?.Mode + " (on|off|toggle|add)");
            }

            Workspace workspace = ResolveWorkspace(request.Target);
            switch (mode)
            {
                case "on":
                    if (model.SoleFocusedWorkspaceId != workspace.Id)
                    {
                        model.ToggleFocusWorkspace(workspace.Id);
                    }

                    break;
                case "off":
                    model.SetFocusMember(workspace.Id, false);
                    break;
                case "toggle":
                    model.ToggleFocusWorkspace(workspace.Id);
                    break;
                case "add":
                    model.SetFocusMember(workspace.Id, true);
                    break;
            }

            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        private ControlResponse FocusFilter(ControlRequest request)
        {
            // agterm's setFocusEnabled refuses to turn the filter on over an empty set, and answers no result.
            bool? wanted = OnOffToggle(request.Args?.Mode, "workspace filter");
            if ((wanted ?? !model.FocusEnabled) && !model.Workspaces.Any(workspace => model.IsFocusMember(workspace.Id)))
            {
                return Ok();
            }

            model.SetFocusEnabled(wanted);
            return Ok();
        }

        private ControlResponse GoSession(ControlRequest request)
        {
            // agterm's SessionNavigation: --to is read first, previous and previous-attention spelled out too.
            string? to = request.Args?.To switch
            {
                "previous" => "prev",
                "previous-attention" => "prev-attention",
                string given => given,
                null => null,
            };
            if (to is not ("next" or "prev" or "first" or "last" or "next-attention" or "prev-attention"))
            {
                return Fail("session.go requires --to next|prev|first|last|next-attention|prev-attention");
            }

            List<Session> sessions = model.NavigableSessions.ToList();
            if (sessions.Count == 0)
            {
                return Fail("no session to navigate");
            }

            switch (to)
            {
                case "next":
                    model.SelectRelative(1);
                    break;
                case "prev":
                    model.SelectRelative(-1);
                    break;
                case "first":
                    model.SelectSession(sessions[0].Id);
                    break;
                case "last":
                    model.SelectSession(sessions[^1].Id);
                    break;
                case "next-attention":
                case "prev-attention":
                    if (model.FindAttention(to == "next-attention") is Guid attention)
                    {
                        model.SelectSession(attention);
                    }

                    break;
                default:
                    return Fail("session.go requires --to next|prev|first|last|next-attention|prev-attention");
            }

            return Ok(new ControlResult { Id = model.SelectedSessionId?.Wire() });
        }

        private ControlResponse MoveSessions(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (args.After is not null && args.Before is not null)
            {
                return Fail("use either --after or --before, not both");
            }

            if (args.After is not null || args.Before is not null)
            {
                if (args.To is not null)
                {
                    return Fail("session.move takes --after/--before or --to, not both");
                }

                if (args.Workspace is not null)
                {
                    return Fail("session.move takes --after/--before or a workspace, not both");
                }
            }
            else if (args.To is not null && args.Workspace is not null)
            {
                return Fail("session.move takes either --to or a workspace, not both");
            }
            else if (args.To is not null)
            {
                if (args.To is not ("up" or "down" or "top" or "bottom"))
                {
                    return Fail("session.move --to must be up|down|top|bottom");
                }

                if (args.Targets is not null)
                {
                    return Fail("session.move --target can be repeated only with a workspace or --after/--before");
                }
            }
            else if (args.Workspace is null)
            {
                return Fail("session.move requires --to or a workspace");
            }

            if (args.Targets is { Count: 0 })
            {
                return Fail("session.move requires at least one --target");
            }

            List<Session> sessions = ResolveSessions(request);
            if (args.To is not null)
            {

                Workspace owner = model.FindWorkspaceOf(sessions[0].Id)!;
                int index = owner.Sessions.ToList().IndexOf(sessions[0]);
                model.MoveSession(sessions[0].Id, owner.Id, Reorder(args.To, index, owner.Sessions.Count, "session.move"));
                return Ok(new ControlResult { Id = sessions[0].Id.Wire() });
            }

            bool batch = (args.Targets?.Count ?? 0) > 1;
            if (args.Workspace is not null)
            {
                // agterm's moveSessions without an index: a batch leaves those already there and appends the rest in tree
                // order, one target appends as moveSession does.
                int affected = model.MoveSessions(sessions.Select(session => session.Id), ResolveWorkspace(args.Workspace).Id);
                return Ok(batch ? new ControlResult { Affected = affected } : new ControlResult { Id = sessions[0].Id.Wire() });
            }

            // agterm's placement at an anchor: the sidebar's drop math, then one move.
            Session anchor = ResolveSession(args.After ?? args.Before);
            Workspace anchorOwner = model.FindWorkspaceOf(anchor.Id)!;
            bool after = args.After is not null;
            if (batch)
            {
                List<Guid> ids = sessions.Select(session => session.Id).ToList();
                SidebarDrop.SessionResolution? block = model.ResolveSessionDrop(ids, anchorOwner.Id, anchor.Id, after);
                return Ok(new ControlResult { Affected = block is null ? 0 : model.MoveSessions(ids, block.Workspace, block.Destination) });
            }

            Workspace home = model.FindWorkspaceOf(sessions[0].Id)!;
            SidebarDrop.SessionSource source = new SidebarDrop.SessionSource(home.Id, home.SessionList.IndexOf(sessions[0]));
            if (SidebarDrop.ResolveRelative(source, anchorOwner.Id, anchorOwner.SessionList.IndexOf(anchor), anchorOwner.SessionList.Count, after) is SidebarDrop.SessionResolution move)
            {
                model.MoveSession(sessions[0].Id, move.Workspace, move.Destination);
            }

            return Ok(new ControlResult { Id = sessions[0].Id.Wire() });
        }

        private ControlResponse RevealSession(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            return host.Reveal(session.Id) ? Ok(new ControlResult { Id = session.Id.Wire() }) : Fail("session cwd is not an existing directory");
        }

        /// <summary>
        /// agterm's typeSession: the text, empty included, typed into a pane as the user would. The arguments are read
        /// before the session; a main pane not up yet is waited for a moment (selected first with --select), a split or
        /// scratch is not. The answer names the pane when --pane-id found it.
        /// </summary>
        private async Task<ControlResponse> TypeAsync(ControlRequest request)
        {
            if (request.Args?.Text is not string text)
            {
                return Fail("session.type requires text");
            }

            if (text.Contains('\0'))
            {
                return Fail("text must not contain a NUL byte");
            }

            if (request.Args.Pane is string named && !Panes.TryParse(named, out _))
            {
                return Fail("invalid pane: " + named);
            }

            Session session = ResolveSession(request.Target);
            PaneRole? byId = PaneById(session, request.Args, alwaysFallBack: false);
            PaneRole pane = byId ?? ResolvePane(session, request.Args.Pane, PaneRole.Main);
            RequireUncovered(session, pane);
            if (FromDaemon(session, pane))
            {
                return await TypeThroughDaemonAsync(session, pane, text, byId);
            }

            // A command or remote pane held after its process exited ends at a keystroke, typed or sent.
            if (text.Length > 0 && host.ReleaseHeld(session.Id, pane))
            {
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            if (!host.IsRunning(session.Id, pane))
            {
                if (pane != PaneRole.Main)
                {
                    return Fail("session not realized");
                }

                if (request.Args.Select == true)
                {
                    model.SelectSession(session.Id);
                }

                // agterm's poll: twelve looks thirty milliseconds apart for the main pane to come up.
                for (int attempt = 0; attempt < 12 && !host.IsRunning(session.Id, pane); attempt++)
                {
                    await Task.Delay(30);
                }

                if (!host.IsRunning(session.Id, pane))
                {
                    return Fail("session not realized");
                }
            }

            // Typing answers a blocked or completed agent, the same as typing by hand; nothing typed answers nothing.
            if (text.Length > 0)
            {
                model.ClearStatusByInput(session.Id, AgentStatuses.ClassifyTyped(text), pane);
            }

            // agterm commits a composition under way first, or the half-typed word would land after the typed text.
            try
            {
                await host.TerminalAsync(session.Id, pane, "commitComposition", null);
            }
            catch (ControlException)
            {
                // A pane with no terminal on the page has no composition either.
            }

            Keystrokes keystrokes = Keystrokes.From(text);
            host.Write(session.Id, pane, keystrokes.Head);
            if (keystrokes.PacedReturn)
            {
                await Task.Delay(Keystrokes.SubmitGap);
                host.Write(session.Id, pane, "\r");
            }

            return Ok(new ControlResult { Id = session.Id.Wire(), Pane = byId is PaneRole found ? Panes.Name(found) : null });
        }

        /// <summary>
        /// agterm's coveredType: a live pane whose daemon reported a role takes scripted input through the daemon, which
        /// encodes each Return for the program's keyboard mode, answers whether it took the input and leaves the lead where
        /// it is, never through the pane's own terminal, whose keystrokes are dropped while it is covered.
        /// </summary>
        private async Task<ControlResponse> TypeThroughDaemonAsync(Session session, PaneRole pane, string text, PaneRole? byId)
        {
            // A half-typed word goes first and on the same acknowledged path: left in place it would commit after the
            // scripted line, and committed through the pane the daemon may drop it.
            string composition = string.Empty;
            try
            {
                composition = await host.TerminalAsync(session.Id, pane, "takeComposition", null) ?? string.Empty;
            }
            catch (ControlException)
            {
                // A pane with no terminal on the page has no composition either.
            }

            Keystrokes keystrokes = Keystrokes.From(composition + text);
            if (keystrokes.Head.Length > 0 && !await host.PaneTypeAsync(session.Id, pane, keystrokes.Head))
            {
                return Fail("the pane's daemon did not accept the input");
            }

            if (keystrokes.PacedReturn)
            {
                await Task.Delay(Keystrokes.SubmitGap);
                // A failed call does not prove the Return was dropped: the daemon may have written it before it answered.
                if (!await host.PaneTypeAsync(session.Id, pane, "\r"))
                {
                    return Fail("text typed, but its final Return could not be confirmed; do not retype the text");
                }
            }

            // Typing answers a blocked or completed agent, the same as typing by hand; nothing typed answers nothing.
            if (text.Length > 0)
            {
                model.ClearStatusByInput(session.Id, AgentStatuses.ClassifyTyped(text), pane);
            }

            return Ok(new ControlResult { Id = session.Id.Wire(), Pane = byId is PaneRole found ? Panes.Name(found) : null });
        }

        private async Task<ControlResponse> ReadTextAsync(ControlRequest request)
        {
            // agterm's parseBufferExtent comes before the session.
            bool all = request.Args?.All == true;
            int? lines = request.Args?.Lines;
            if (all && lines is not null)
            {
                return Fail("use either --all or --lines, not both");
            }

            if (lines <= 0)
            {
                return Fail("--lines must be greater than 0");
            }

            if (request.Args?.Pane is string named && !Panes.TryParse(named, out _))
            {
                return Fail("invalid pane: " + named);
            }

            Session session = ResolveSession(request.Target);
            PaneRole pane = PaneById(session, request.Args, alwaysFallBack: false) ?? ResolvePane(session, request.Args?.Pane, session.FocusedPane);
            RequireUncovered(session, pane);
            string text;
            if (FromDaemon(session, pane, viewport: !all && lines is null))
            {
                // agterm's coveredText: --lines reads the whole buffer and keeps its last lines.
                DaemonScreen screen = await host.PaneScreenAsync(session.Id, pane, all || lines is not null) ?? throw new ControlException("failed to read surface buffer");
                text = lines is int count ? screen.LastLines(count) : screen.Text;
            }
            else
            {
                text = await host.ReadTextAsync(session.Id, pane, all, lines);
            }

            return Ok(new ControlResult { Text = text, Pane = Panes.Name(pane) });
        }

        private async Task<ControlResponse> SearchAsync(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            string? step = request.Args?.To;
            if (step is not null && step != "next" && step != "prev" && step != "close")
            {
                return Fail("session.search --to must be next|prev|close");
            }

            // agterm: closing is never refused; anything else waits for an open modal and leaves the terminal zoom alone.
            if (step != "close" && model.PendingModalError is string modal)
            {
                return Fail(modal);
            }

            if (step != "close" && model.Zoomed)
            {
                return Fail("terminal zoom active");
            }

            // agterm drives the search that is open, on the pane it was opened on; a new one opens on the pane on screen.
            PaneRole searched = host.SearchPane(session.Id) is PaneRole open ? open : session.FocusedPane;
            if (step == "close")
            {
                // Closing a search reads nothing, so agterm allows it on a covered pane.
                await host.SearchAsync(session.Id, searched, null, "close");
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            RequireUncovered(session, searched, hereToo: true);
            // agterm selects the session, so the bar and its highlights are on screen.
            model.SelectSession(session.Id);
            string raw = await host.SearchAsync(session.Id, searched, request.Args?.Text, step);
            if (request.Args?.Text == string.Empty)
            {
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            // The page answers "count index", -1 for none: agterm's searchDisplayText and its count.
            string[] parts = raw.Split(' ');
            int total = parts.Length == 2 && int.TryParse(parts[0], out int parsedTotal) ? parsedTotal : -1;
            int selected = parts.Length == 2 && int.TryParse(parts[1], out int parsedSelected) ? parsedSelected : -1;
            string? display = total < 0 ? null : total == 0 ? "no matches" : selected < 0 ? total + " matches" : Math.Min(selected + 1, total) + " of " + total;
            return Ok(new ControlResult { Text = display, Count = total < 0 ? null : total });
        }

        /// <summary>
        /// agterm's session.restart, for a pane named by --pane or --pane-id. A remote row is refused whole, its split of this
        /// machine included. Unlike agterm it works without live sessions too: this machine owns its shells either way.
        /// </summary>
        private async Task<ControlResponse> RestartAsync(ControlRequest request)
        {
            // agterm checks the line first: it is run as it is, so an empty one or one with control characters is refused.
            ControlArgs args = request.Args ?? new ControlArgs();
            if (args.Command is string line)
            {
                if (line.Trim().Length == 0)
                {
                    return Fail("session.restart takes a non-empty command, or none to replay");
                }

                if (CommandRestore.HasControlCharacter(line))
                {
                    return Fail("command must not contain control characters");
                }

                if (System.Text.Encoding.UTF8.GetByteCount(line) > MaxRestartCommandBytes)
                {
                    return Fail("command too long (max " + MaxRestartCommandBytes + " bytes)");
                }
            }

            // agterm's dispatcher: --pane is read whenever given, --pane-id or not, before the session.
            PaneRole? named = null;
            if (args.Pane is string paneName)
            {
                if (!Panes.TryParse(paneName, out PaneRole parsed))
                {
                    return Fail("--pane must be left, right, or scratch");
                }

                named = parsed;
            }

            if (named == PaneRole.Scratch)
            {
                return Fail("session.restart does not address the scratch pane");
            }

            if (named is null && string.IsNullOrEmpty(args.PaneId))
            {
                return Fail("session.restart requires --pane-id or --pane");
            }

            Session session = ResolveSession(request.Target);
            if (session.RemoteHost is string machine)
            {
                return Fail("session.restart needs a local pane; this session runs on " + machine);
            }

            // A token that resolves to nothing is refused even beside --pane: the role may name another terminal by now.
            PaneRole? pane = named;
            if (args.PaneId is { Length: > 0 } token)
            {
                pane = AppModel.PaneOfId(session, token) ?? throw new ControlException("unknown pane id: " + token);
            }

            if (pane is not (PaneRole.Main or PaneRole.Split))
            {
                return Fail("session.restart does not address the scratch pane");
            }

            if (pane == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split pane");
            }

            (string? error, ControlRestartReceipt? receipt) = await host.RestartPaneAsync(session.Id, pane.Value, args.Command);
            if (error is not null || receipt is null)
            {
                return Fail(error ?? "the pane could not be restarted");
            }

            string side = Panes.Name(pane.Value);
            string replayed = receipt.ReplayedArgv is { Count: > 0 } argv ? "; replay requested: " + CommandRestore.PosixJoin(argv) : string.Empty;
            return Ok(new ControlResult
            {
                Id = session.Id.Wire(),
                Text = "restarted " + side + " pane: shell " + (receipt.OldPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(ended)") + " -> " + receipt.NewPid + replayed,
                Pane = side,
                Restart = receipt,
            });
        }

        /// <summary>agterm's cap on a pinned restore line, which is saved with the window.</summary>
        private const int MaxRestoreCommandBytes = 1024;

        /// <summary>agterm's cap on the line a restarted shell runs.</summary>
        private const int MaxRestartCommandBytes = 4096;

        private ControlResponse Split(ControlRequest request)
        {
            // agterm: the axis is read before the session, the mode after it.
            SplitAxis? axis = null;
            if (request.Args?.Axis is string value)
            {
                axis = Panes.TryParseAxis(value, out SplitAxis parsed) ? parsed : throw new ControlException("invalid split axis: " + value + " (vertical|horizontal)");
            }

            Session session = ResolveSession(request.Target);
            bool? shown = ParseToggle(request.Args?.Mode, "on", "off", "split");

            model.SetSplit(session.Id, shown, axis);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse CloseSplit(ControlRequest request)
        {
            // agterm: closing a split that is not there is done already.
            Session session = ResolveSession(request.Target);
            if (!session.HasSplit)
            {
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            host.StopPane(session.Id, PaneRole.Split);
            model.CloseSplit(session.Id);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse Swap(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            if (!model.SwapPanes(session.Id))
            {
                return Fail("session has no split");
            }

            host.SwapPaneShells(session.Id);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse Focus(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            if (!session.HasSplit)
            {
                return Fail("session has no split");
            }

            // agterm's ControlPaneFocusMode: toggle is other's other name.
            string value = request.Args?.Pane is null or "toggle" ? "other" : request.Args.Pane;
            PaneRole pane = value == "other"
                ? (session.SplitFocused ? PaneRole.Main : PaneRole.Split)
                : Panes.TryParse(value, out PaneRole parsed) && parsed != PaneRole.Scratch ? parsed : throw new ControlException("invalid pane: " + value);
            model.FocusPane(session.Id, pane);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse Resize(ControlRequest request)
        {
            // agterm checks the arguments before the session.
            switch (request.Args?.Ratio, request.Args?.RatioDelta)
            {
                case (null, null):
                    return Fail("session.resize requires --split-ratio, --grow-left, or --grow-right");
                case (not null, not null):
                    return Fail("session.resize: --split-ratio is mutually exclusive with --grow-left/--grow-right");
            }

            Session session = ResolveSession(request.Target);
            if (!session.HasSplit)
            {
                return Fail("session has no split");
            }

            double requested = request.Args!.Ratio ?? (session.SplitRatio ?? Panes.DefaultSplitRatio) + request.Args.RatioDelta!.Value;

            model.SetSplitRatio(session.Id, requested);
            return Ok(new ControlResult { Id = session.Id.Wire(), Ratio = session.SplitRatio });
        }

        private ControlResponse Scratch(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            bool want = ParseToggle(request.Args?.Mode, "on", "off", "scratch") ?? !session.ScratchShown;
            string? command = request.Args?.Command;
            bool running = host.IsRunning(session.Id, PaneRole.Scratch);
            // agterm: replacing a visible scratch's program ends shown as it began, so neither step emits pane.scratch.
            bool respawningVisible = want && session.ScratchShown && running && !string.IsNullOrEmpty(command);
            if (want && !string.IsNullOrEmpty(command))
            {
                if (running)
                {
                    host.StopPane(session.Id, PaneRole.Scratch);
                    model.ScratchClosed(session.Id, emit: !respawningVisible);
                }

                model.SetScratchCommand(session.Id, command);
            }

            if (want && request.Args?.Follow == true && model.SelectedSessionId != session.Id)
            {
                model.SelectSession(session.Id);
            }

            model.SetScratch(session.Id, want, emit: !respawningVisible);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>
        /// The overlay slot a command addresses: the whole session, or with --pane one pane of a split, as in agterm.
        /// The right pane must be on screen.
        /// </summary>
        /// <summary>
        /// The overlay slot a command names. A hidden split's slot counts for the commands agterm allows on any existing pane
        /// (overlay open, close and result), so a job handed out over it can still be read or canceled.
        /// </summary>
        private static PaneRole ResolveSlot(Session session, string? pane, bool hiddenOk = false)
        {
            if (SlotPane(pane) is not PaneRole role)
            {
                return PaneRole.Overlay;
            }

            if (role == PaneRole.Split && !(session.RendersPane(PaneRole.Split) || (hiddenOk && session.HasSplit)))
            {
                throw new ControlException("pane not visible");
            }

            // agterm's rendersPane: the left pane is off screen while a hidden split shows the right one maximized.
            if (role == PaneRole.Main && !hiddenOk && !session.RendersPane(PaneRole.Main))
            {
                throw new ControlException("pane not visible");
            }

            return Panes.OverlayOf(role);
        }

        /// <summary>agterm's parseOverlayPane: the pane an overlay command names, read before the session is looked up.</summary>
        private static PaneRole? SlotPane(string? pane)
        {
            if (pane is null)
            {
                return null;
            }

            return Panes.TryParse(pane, out PaneRole role) && role != PaneRole.Scratch ? role : throw new ControlException("session.overlay: --pane must be left or right");
        }

        /// <summary>The program or page in a slot; a HUD does not count.</summary>
        /// <summary>agterm's tree hud node: what the panel shows and where, the configured hide-after rather than the time left.</summary>
        private ControlHudNode HudNode(SessionOverlay panel, HudSpec hud)
        {
            (int Width, int Height)? measured = host.HudGeometry(panel.Id);
            return new ControlHudNode
            {
                Message = hud.Message,
                Detail = hud.Detail,
                Spinner = hud.Spinner ?? HudSpinners.None,
                BackgroundColor = panel.BackgroundColor,
                TextColor = hud.TextColor,
                // agterm reports the width as the panel may take it, bounded like an asked one.
                SizePercent = panel.SizePercent ?? (measured is { Width: int width } ? HudSpec.ClampWidth(width, hud) : null),
                HeightPercent = measured?.Height,
                Position = hud.Position,
                Pane = panel.HudPane is PaneRole pane ? Panes.Name(pane) : null,
                HideAfter = hud.HideAfter ?? 0,
                Markdown = hud.Markdown,
                FontSize = hud.FontSize,
                Sticky = hud.Sticky,
                Frame = hud.Frame,
            };
        }

        /// <summary>agterm's htmlOverlays: each page by slot, with what it was opened with and what its view shows now.</summary>
        private List<ControlHtmlOverlayNode>? HtmlOverlaysOf(Session session)
        {
            List<ControlHtmlOverlayNode> pages = new[] { PaneRole.Overlay, PaneRole.OverlayLeft, PaneRole.OverlayRight }
                .Where(slot => session.Overlays.GetValueOrDefault(slot) is { Page: not null })
                .Select(slot =>
                {
                    SessionOverlay overlay = session.Overlays[slot];
                    PageSpec page = overlay.Page!;
                    ControlPageView? shown = host.PageState(overlay.Id);
                    return new ControlHtmlOverlayNode
                    {
                        Id = overlay.Id,
                        Pane = slot == PaneRole.Overlay ? null : slot == PaneRole.OverlayLeft ? "left" : "right",
                        File = page.IsFile ? page.Source : null,
                        // agterm's replica of a file page names its file alone: the folder is the other machine's.
                        Cwd = page.IsFile && !host.IsReplicaPage(overlay.Id) ? page.Root : null,
                        Url = page.IsFile ? null : page.Source,
                        State = shown?.State ?? "loading",
                        Error = shown?.Error,
                        Page = shown?.Page,
                        Title = shown?.Title,
                        CanGoBack = shown?.CanGoBack,
                        CanGoForward = shown?.CanGoForward,
                        Zoom = shown?.Zoom,
                        Navigation = page.Navigation ? true : null,
                        Hidden = overlay.Hidden ? true : null,
                        Javascript = page.Js,
                        Chromeless = page.Chromeless,
                        Persistent = page.Persistent,
                        Browse = page.Browse,
                        Replica = host.IsReplicaPage(overlay.Id) ? true : null,
                    };
                })
                .ToList();
            // agterm's remote pages: shown on the presenter, reported as it last said.
            pages.AddRange(model.RemotePagesOf(session.Id).Select(remote => new ControlHtmlOverlayNode
            {
                Id = remote.Id,
                Pane = remote.Slot == PaneRole.Overlay ? null : remote.Slot == PaneRole.OverlayLeft ? "left" : "right",
                File = remote.Spec.IsFile ? remote.Spec.Source : null,
                Cwd = remote.Spec.IsFile ? remote.Spec.Root : null,
                Url = remote.Spec.IsFile ? null : remote.Spec.Source,
                State = remote.Reported?.State ?? "loading",
                Error = remote.Reported?.Error,
                Page = remote.Reported?.Page,
                Title = remote.Reported?.Title,
                CanGoBack = remote.Reported?.CanGoBack,
                CanGoForward = remote.Reported?.CanGoForward,
                Navigation = remote.Spec.Navigation ? true : null,
                Hidden = remote.Reported?.Hidden == true ? true : null,
                Javascript = remote.Spec.Js,
                Chromeless = remote.Spec.Chromeless,
                Persistent = remote.Spec.Persistent,
                Browse = remote.Spec.Browse,
                Remote = true,
            }));
            // agterm lists by slot — the session's, the left pane's, the right pane's — whichever machine shows the page.
            pages = pages.OrderBy(node => node.Pane is null ? 0 : node.Pane == "left" ? 1 : 2).ToList();
            return pages.Count == 0 ? null : pages;
        }

        private SessionOverlay RequireProgram(Session session, PaneRole slot)
        {
            if (session.Overlays.TryGetValue(slot, out SessionOverlay? overlay) && !overlay.IsHud)
            {
                return overlay;
            }

            // agterm: an overlay the presenting machine draws has nothing here to read or drive.
            throw new ControlException(model.OverlaySlotReserved(session.Id, slot)
                ? "overlay is shown on another machine"
                : "no overlay");
        }

        /// <summary>
        /// agterm's overlay read target: an overlay shown on another machine is refused first, then a page or a HUD, which
        /// run no program of the caller's to read.
        /// </summary>
        private void RequireReadable(Session session, PaneRole slot)
        {
            if (model.RemotePageAt(session.Id, slot) is not null)
            {
                throw new ControlException("no overlay to read: the slot holds an html page");
            }

            if (model.OverlaySlotReserved(session.Id, slot))
            {
                throw new ControlException("overlay is shown on another machine");
            }

            if (!session.Overlays.TryGetValue(slot, out SessionOverlay? overlay))
            {
                throw new ControlException("no overlay");
            }

            if (overlay.Page is not null)
            {
                throw new ControlException("no overlay to read: the slot holds an html page");
            }

            if (overlay.IsHud)
            {
                throw new ControlException("no overlay to read: the slot holds a hud");
            }
        }

        /// <summary>agterm's page lookup: an empty slot or a HUD is no overlay, a program is not a page.</summary>
        private static SessionOverlay RequirePage(Session session, PaneRole slot)
        {
            if (!session.Overlays.TryGetValue(slot, out SessionOverlay? overlay) || overlay.IsHud)
            {
                throw new ControlException("no overlay");
            }

            return overlay.Page is not null ? overlay : throw new ControlException("the overlay is not an html page");
        }

        private ControlResponse PageCommand(ControlRequest request, string action)
        {
            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane);
            if (model.RemotePageAt(session.Id, slot) is RemotePage remote)
            {
                model.QueuePageCommand(remote, action);
                return Ok(new ControlResult { Id = session.Id.Wire()});
            }

            SessionOverlay overlay = RequirePage(session, slot);
            if (host.PageCommand(session.Id, slot, action, null) is string failure)
            {
                return Fail(failure);
            }

            return Ok(new ControlResult { Id = session.Id.Wire()});
        }

        private ControlResponse Navigate(ControlRequest request)
        {
            string? to = request.Args?.To;
            string? url = request.Args?.Url;
            // agterm's steps; loading a URL in a web page is this machine's own addition.
            if ((to is null) == (url is null) || (to is not null && to is not ("back" or "forward" or "browser" or "finder")))
            {
                return Fail("session.overlay.navigate requires back, forward, browser or finder");
            }

            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane);

            // agterm: the presenter steps a page shown there and opens a web page in its browser; a file page has no file there.
            if (model.RemotePageAt(session.Id, slot) is RemotePage remote)
            {
                // agterm: a web page has nothing to show in Finder wherever it is shown.
                if (to == "finder" && !remote.Spec.IsFile)
                {
                    return Fail("show in Finder requires a file page");
                }

                if (to is null or "finder" || (to == "browser" && remote.Spec.IsFile))
                {
                    return Fail("page is shown on another machine");
                }

                model.QueuePageCommand(remote, to);
                return Ok(new ControlResult { Id = session.Id.Wire()});
            }

            SessionOverlay overlay = RequirePage(session, slot);

            if (to == "finder" && !overlay.Page!.IsFile)
            {
                return Fail("show in Finder requires a file page");
            }

            if (url is not null && (overlay.Page!.IsFile || !IsWebUrl(url)))
            {
                return Fail("a URL can only be loaded in a web page overlay, and must be http or https");
            }

            if (host.PageCommand(session.Id, slot, to ?? "navigate", url) is string failure)
            {
                return Fail(failure);
            }

            return Ok(new ControlResult { Id = session.Id.Wire()});
        }

        private ControlResponse Submit(ControlRequest request)
        {
            // agterm: the answer is text, and it must be there; empty is a real answer.
            if (request.Args?.Value is not string value)
            {
                return Fail("session.overlay.submit requires a value");
            }

            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane);

            if (model.RemotePageAt(session.Id, slot) is RemotePage remote)
            {
                model.SubmitRemotePage(remote, value);
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            // agterm: a page shown here for another machine is answered there, through the page itself.
            if (host.IsReplicaPage(RequirePage(session, slot).Id))
            {
                return Fail("submit on the machine that opened the page");
            }

            model.SubmitPage(session.Id, slot, value);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private static bool IsWebUrl(string value)
        {
            return ControlUrls.IsWeb(value);
        }

        private static bool IsLoopback(string value)
        {
            return ControlUrls.IsLoopback(value);
        }

        /// <summary>Checks a page request the way agterm does and builds what the overlay shows.</summary>
        private static PageSpec? PageOf(ControlArgs args)
        {
            // agterm's OverlayHtmlError, one refusal per combination.
            const string open = "session.overlay.open";
            bool command = !string.IsNullOrWhiteSpace(args.Command);
            bool html = !string.IsNullOrWhiteSpace(args.Html);
            bool url = !string.IsNullOrWhiteSpace(args.Url);
            if (command && html)
            {
                throw new ControlException(open + " takes a command or --html, not both");
            }

            if (command && url)
            {
                throw new ControlException(open + " takes a command or --url, not both");
            }

            if (html && url)
            {
                throw new ControlException(open + " takes --html or --url, not both");
            }

            if (!command && !html && !url)
            {
                throw new ControlException(open + " requires a command");
            }

            if (command)
            {
                if (args.Navigation == true)
                {
                    throw new ControlException(open + ": --navigation requires --html or --url");
                }

                if (args.Js == true)
                {
                    throw new ControlException(open + ": --js requires --html or --url");
                }

                if (args.Chromeless == true)
                {
                    throw new ControlException(open + ": --chromeless requires --html or a loopback --url");
                }

                return null;
            }

            if (args.Wait == true)
            {
                throw new ControlException(open + ": --wait cannot be combined with " + (html ? "--html" : "--url"));
            }

            if (args.Chromeless == true && args.Navigation == true)
            {
                throw new ControlException(open + ": --chromeless cannot be combined with --navigation");
            }

            if (args.Persistent == true && !url)
            {
                throw new ControlException(open + ": --persistent requires --url");
            }

            if (args.Browse == true && !url)
            {
                throw new ControlException(open + ": --browse requires --url");
            }

            if (args.Chromeless == true && args.Browse == true)
            {
                throw new ControlException(open + ": --chromeless cannot be combined with --browse");
            }

            if (url && !string.IsNullOrWhiteSpace(args.Cwd))
            {
                throw new ControlException(open + ": --cwd cannot be combined with --url");
            }

            if (args.Url is string address)
            {
                if (!IsWebUrl(address))
                {
                    throw new ControlException(open + ": --url must be an absolute http or https URL");
                }

                if (args.Chromeless == true && !IsLoopback(address))
                {
                    throw new ControlException(open + ": --chromeless with --url requires a loopback host: 127.0.0.1, ::1 or localhost");
                }

                return new PageSpec
                {
                    Source = address,
                    Js = args.Js == true,
                    Navigation = args.Navigation == true,
                    Chromeless = args.Chromeless == true,
                    Persistent = args.Persistent == true,
                    Browse = args.Browse == true,
                };
            }

            string file = args.Html!;
            string? root = string.IsNullOrWhiteSpace(args.Cwd) ? null : args.Cwd;
            if (PageSpec.GrantError(file, root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is string refusal)
            {
                throw new ControlException(open + ": " + refusal);
            }

            // As agterm's, a file that is not there opens anyway and its page reports the failed load.
            return new PageSpec
            {
                Source = Path.GetFullPath(file),
                IsFile = true,
                Root = root is null ? null : Path.GetFullPath(root),
                Js = args.Js == true,
                Navigation = args.Navigation == true,
                Chromeless = args.Chromeless == true,
            };
        }

        private ControlResponse OpenOverlay(ControlRequest request)
        {
            // agterm validates everything about the overlay before the session is looked up.
            ControlArgs args = request.Args ?? new ControlArgs();
            PageSpec? page = PageOf(args);
            string? color = ValidColor(args.Color);
            bool paned = SlotPane(args.Pane) is not null;
            if (paned && args.SizePercent is not null)
            {
                return Fail("session.overlay.open: --pane is mutually exclusive with --size-percent");
            }

            if (paned && (args.Width ?? args.Height) is not null)
            {
                return Fail("session.overlay.open: --pane is mutually exclusive with --width/--height");
            }

            int? percent = ValidPercent(args.SizePercent, "session.overlay.open");
            if (args.Width is <= 0 || args.Height is <= 0)
            {
                return Fail("session.overlay: --width/--height must be positive");
            }

            if ((args.Width ?? args.Height) is not null && args.SizePercent is null)
            {
                return Fail("session.overlay.open: --width/--height need --size-percent");
            }

            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, args.Pane, hiddenOk: true);
            SessionOverlay overlay = new SessionOverlay
            {
                Id = Ids.New(),
                Command = page is null ? args.Command : null,
                Page = page,
                Cwd = page is null && !string.IsNullOrWhiteSpace(args.Cwd) ? args.Cwd : null,
                Wait = args.Wait == true,
                SizePercent = percent,
                WidthPoints = args.Width,
                HeightPoints = args.Height,
                BackgroundColor = color,
            };
            if (model.OverlaySlotReserved(session.Id, slot))
            {
                return Fail(slot == PaneRole.Overlay ? "overlay already open" : "pane overlay already open");
            }

            // agterm's openRemotePage: a new page goes to the presenter by the program overlays' rule, checked in its order — a
            // page or program of this machine in the slot, point sizes, a presenter too old to show pages, a loopback address
            // naming a server on this machine, a page too large. The browser store is asked about only for a page shown here.
            bool pageThere = page is not null && model.CanHandOverlayOver(session.Id, slot, role => host.PaneLead(session.Id, role));
            if (pageThere && session.Overlays.TryGetValue(slot, out SessionOverlay? holder) && !holder.IsHud)
            {
                return Fail(slot == PaneRole.Overlay ? "overlay already open" : "pane overlay already open");
            }

            if (pageThere && (overlay.WidthPoints is not null || overlay.HeightPoints is not null))
            {
                return Fail("--width/--height are not supported for an overlay shown on another machine");
            }

            if (pageThere && !model.PresenterShowsPages(session.Id))
            {
                return Fail("a viewer presents this session: an html overlay would open where nobody sees it");
            }

            if (pageThere && !page!.IsFile && IsLoopback(page.Source))
            {
                return Fail("a loopback url names this machine, so the machine presenting the session cannot load it");
            }

            if (!pageThere && page is { Persistent: true } && host.BrowserClearing)
            {
                return Fail("session.overlay.open: browser storage is being cleared");
            }

            bool handOver = overlay.IsProgram && overlay.Command is not null && model.CanHandOverlayOver(session.Id, slot, role => host.PaneLead(session.Id, role));
            if (handOver)
            {
                if (overlay.WidthPoints is not null || overlay.HeightPoints is not null)
                {
                    return Fail("--width/--height are not supported for an overlay shown on another machine");
                }

                // agterm refuses to hand an overlay over a program of this machine's; a HUD gives way.
                if (session.Overlays.TryGetValue(slot, out SessionOverlay? here) && !here.IsHud)
                {
                    return Fail(here.Hidden ? "overlay hidden" : slot == PaneRole.Overlay ? "overlay already open" : "pane overlay already open");
                }

                ControlOverlayJob launch = host.OverlayLaunch(session.Id, slot, overlay.Command!, overlay.Cwd);
                // agterm: the launch context must fit one line of the job's connection.
                if (System.Text.Encoding.UTF8.GetByteCount(ControlJson.Serialize(new ControlOverlayJobFrame { Kind = OverlayJobKinds.Context, Context = launch })) + 1 > PageServe.MaxFrameBytes)
                {
                    return Fail("overlay command too large to show on another machine");
                }

                model.HandOverlayOver(session.Id, overlay, slot, role => host.PaneLead(session.Id, role), DateTimeOffset.UtcNow, args.Follow == true, launch);
                return Ok(new ControlResult { Id = session.Id.Wire()});
            }

            // agterm: a program or page already in the slot keeps it; a HUD gives way.
            if (session.Overlays.TryGetValue(slot, out SessionOverlay? taken) && !taken.IsHud)
            {
                return Fail(taken.Hidden ? "overlay hidden" : slot == PaneRole.Overlay ? "overlay already open" : "pane overlay already open");
            }

            if (pageThere)
            {
                if (!model.HandPageOver(session.Id, overlay, slot, args.Follow == true))
                {
                    return Fail("page too large to show on another machine");
                }

                return Ok(new ControlResult { Id = session.Id.Wire(), PageId = overlay.Id });
            }

            model.OpenOverlay(session.Id, overlay, slot);
            if (args.Follow == true)
            {
                model.SelectSession(session.Id);
            }

            return Ok(new ControlResult { Id = session.Id.Wire(), PageId = overlay.Page is null ? null : overlay.Id });
        }

        /// <summary>
        /// agterm's overlay hide, show and toggle: every program and page of the session, or one pane's with --pane. The reply
        /// says how they were left, hidden or shown, which is how a toggle tells the way it went.
        /// </summary>
        private ControlResponse OverlayVisibility(ControlRequest request, bool? hidden)
        {
            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole? scope = request.Args?.Pane is null ? null : ResolveSlot(session, request.Args.Pane, hiddenOk: true);
            if (model.SetOverlaysHidden(session.Id, scope, hidden) is string failure)
            {
                return Fail(failure);
            }

            return Ok(new ControlResult { Id = session.Id.Wire(), Text = model.AnyOverlayShown(session, scope) ? "shown" : "hidden" });
        }

        private ControlResponse CloseOverlay(ControlRequest request)
        {
            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane, hiddenOk: true);
            if (model.CancelOverlayJob(session.Id, slot))
            {
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            if (model.RemotePageAt(session.Id, slot) is RemotePage remote)
            {
                model.CloseRemotePage(remote);
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            // agterm's close takes a HUD down too, as a courtesy of the shared teardown.
            if (slot == PaneRole.Overlay && session.Overlay is { IsHud: true } hud)
            {
                model.CloseOverlay(session.Id, hud.Id);
                return Ok(new ControlResult { Id = session.Id.Wire() });
            }

            if (!session.Overlays.ContainsKey(slot) && !model.OverlaySlotReserved(session.Id, slot))
            {
                return Fail("no overlay");
            }

            RequireProgram(session, slot);
            host.StopPane(session.Id, slot);
            model.CloseOverlay(session.Id, slot: slot);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse OverlayResult(ControlRequest request)
        {
            // agterm's result --page ID: a page's outcome by its id, kept for the app after its session or window went. A pane
            // beside it says nothing about the page and is ignored, as agterm's.
            if (request.Args?.PageId is string pageId)
            {
                if (!Guid.TryParse(pageId, out _))
                {
                    return Fail("invalid page id");
                }

                if (PageOutcomes.Find(pageId) is not { } found)
                {
                    return Fail("no such page");
                }

                return Ok(new ControlResult { Page = new ControlPageResult { PageId = pageId, Outcome = found.Outcome, Value = PageValue(found.Value) } });
            }

            SlotPane(request.Args?.Pane);

            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane, hiddenOk: true);
            OverlayResult? last = session.LastOverlay(slot);
            // agterm's result: a program still running and one that left no status are errors, so a poll retries on the first
            // and a script reads only real exit codes; a job that ended with no status names how it ended.
            // agterm's order: a job on the presenter first, then a page covering the slot, which runs no program to wait for.
            if (model.RemotePageAt(session.Id, slot) is not null)
            {
                return Fail("no overlay result: the slot holds an html page");
            }

            if (last is { Remote: true, IsPage: false, Failure: null, ExitCode: null })
            {
                return Fail(OverlayResultErrors.StillRunning, new ControlResult { Id = session.Id.Wire()});
            }

            if (session.Overlays.GetValueOrDefault(slot) is { Page: not null })
            {
                return Fail("no overlay result: the slot holds an html page");
            }

            if (last is { Remote: false, Failure: null, ExitCode: null } && session.Overlays.TryGetValue(slot, out SessionOverlay? current) && current.Id == last.Id)
            {
                return Fail(OverlayResultErrors.StillRunning, new ControlResult { Id = session.Id.Wire()});
            }

            if (last?.Failure is string failure)
            {
                return Fail("overlay ended: " + failure);
            }

            // A HUD opened after a program clears its result here; a remote job's that ended under a HUD still reads.
            if (session.Overlays.GetValueOrDefault(slot) is { IsHud: true } && (last is null || !last.Remote))
            {
                return Fail("no overlay result: the slot holds a hud");
            }

            if (last?.ExitCode is not int code)
            {
                return Fail(OverlayResultErrors.NoResult);
            }

            return Ok(new ControlResult { Id = session.Id.Wire(), ExitCode = code });
        }

        /// <summary>A submitted value, as the page gave it.</summary>
        private static string? PageValue(string? value)
        {
            return value;
        }

        private ControlResponse ResizeOverlay(ControlRequest request)
        {
            if (request.Args?.Pane is not null)
            {
                return Fail("session.overlay.resize: --pane is not supported (pane overlays are always full)");
            }

            bool full = request.Args?.Full == true;
            int? width = request.Args?.Width;
            int? height = request.Args?.Height;
            bool points = (width ?? height) is not null;
            if (full && request.Args?.SizePercent is not null)
            {
                return Fail("session.overlay.resize: --full is mutually exclusive with --size-percent");
            }

            if (full && points)
            {
                return Fail("session.overlay.resize: --full is mutually exclusive with --width/--height");
            }

            if (!full && request.Args?.SizePercent is null && !points)
            {
                return Fail("session.overlay.resize requires --size-percent, --width/--height or --full");
            }

            int? percent = ValidPercent(request.Args?.SizePercent, "session.overlay.resize");
            if (width is <= 0 || height is <= 0)
            {
                return Fail("session.overlay: --width/--height must be positive");
            }

            Session session = ResolveSession(request.Target);

            if (points && model.OverlaySlotReserved(session.Id, PaneRole.Overlay))
            {
                return Fail("--width/--height are not supported for an overlay shown on another machine");
            }

            if (model.RemotePageAt(session.Id, PaneRole.Overlay) is RemotePage remotePage)
            {
                model.ResizeRemotePage(remotePage, percent);
                return Ok(new ControlResult { Id = session.Id.Wire()});
            }

            switch (model.ResizeOverlayJob(session.Id, percent))
            {
                case true:
                    return Ok(new ControlResult { Id = session.Id.Wire()});
                case false:
                    return Fail("the viewer showing this overlay is gone");
            }

            // agterm: a HUD is always floating; its width is bounded as the panel's own and reaches the viewers.
            if (session.Overlay is { IsHud: true } hud)
            {
                if (points)
                {
                    return Fail("a hud measures its own height: --width/--height do not apply");
                }

                if (percent is not int hudWidth)
                {
                    return Fail("a hud is always floating: pass --size-percent, not --full");
                }

                model.UpdateOverlay(session.Id, current => current with { SizePercent = HudSpec.ClampWidth(hudWidth, current.Hud!) });
                return Ok(new ControlResult { Id = session.Id.Wire()});
            }

            SessionOverlay overlay = RequireProgram(session, PaneRole.Overlay);
            if (points && percent is null && overlay.SizePercent is null)
            {
                return Fail("session.overlay.resize: --width/--height need a floating overlay; pass --size-percent too");
            }

            // agterm's rule: with a percent the panel takes exactly the point axes given; points alone change only theirs;
            // a percent alone or --full drops them.
            model.UpdateOverlay(session.Id, current => percent is not null || full
                ? current with { SizePercent = percent, WidthPoints = width, HeightPoints = height }
                : current with { WidthPoints = width ?? current.WidthPoints, HeightPoints = height ?? current.HeightPoints });
            return Ok(new ControlResult { Id = session.Id.Wire()});
        }

        /// <summary>agterm's overlay copy: the selection made inside the overlay, which session copy cannot reach.</summary>
        private async Task<ControlResponse> OverlayCopyAsync(ControlRequest request)
        {
            SlotPane(request.Args?.Pane);
            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane);
            RequireReadable(session, slot);
            string? text = await host.TerminalAsync(session.Id, slot, "copy", null);
            return text switch
            {
                null => Fail("overlay not realized"),
                "" => Fail("no selection"),
                _ => Ok(new ControlResult { Text = text }),
            };
        }

        /// <summary>agterm's readSessionOverlayText: the extent checked as session text checks it, before the session.</summary>
        private async Task<ControlResponse> OverlayTextAsync(ControlRequest request)
        {
            bool all = request.Args?.All == true;
            int? lines = request.Args?.Lines;
            if (all && lines is not null)
            {
                return Fail("use either --all or --lines, not both");
            }

            if (lines <= 0)
            {
                return Fail("--lines must be greater than 0");
            }

            SlotPane(request.Args?.Pane);

            Session session = ResolveSession(request.Target);
            PaneRole slot = ResolveSlot(session, request.Args?.Pane);
            RequireReadable(session, slot);
            string text;
            try
            {
                text = await host.ReadTextAsync(session.Id, slot, all, lines);
            }
            catch (ControlException ex) when (ex.Message == "session not realized")
            {
                return Fail("overlay not realized");
            }

            return Ok(new ControlResult { Text = text });
        }

        private ControlResponse OpenHud(ControlRequest request, bool update)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            string command = update ? "session.hud.update" : "session.hud.open";
            // agterm checks the pane's spelling and the arguments first, then the session, where the panel goes and whether
            // the slot is free.
            PaneRole? named = null;
            if (args.Pane is string paneName)
            {
                named = Panes.TryParse(paneName, out PaneRole parsed) && parsed is PaneRole.Main or PaneRole.Split ? parsed : throw new ControlException("--pane must be left or right");
            }

            if (string.IsNullOrWhiteSpace(args.Message))
            {
                return Fail(command + " requires a message");
            }

            // The panel prints straight into a terminal: no escape may paint outside it. Markdown needs its newlines and tabs.
            bool markdown = args.Markdown == true;
            if (args.Message.Any(c => (c < 0x20 && !(markdown && c is '\n' or '\t')) || c == 0x7f) || (args.Detail ?? string.Empty).Any(c => c < 0x20 || c == 0x7f))
            {
                return Fail("hud text must not contain control characters");
            }

            int cap = markdown ? HudSpec.MaxMarkdownLength : HudSpec.MaxTextLength;
            if (HudSpec.TextLength(args.Message) > cap)
            {
                return Fail("hud message too long (max " + cap + " characters)");
            }

            if (markdown && !HudMarkdown.RendersVisibleText(args.Message))
            {
                return Fail(command + " requires a message");
            }

            if (HudSpec.TextLength(args.Detail ?? string.Empty) > HudSpec.MaxTextLength)
            {
                return Fail("hud detail too long (max " + HudSpec.MaxTextLength + " characters)");
            }

            if (args.Color is string backgroundHex && !IsHexColor(backgroundHex))
            {
                return Fail("invalid color: " + backgroundHex + " (#rrggbb)");
            }

            if (args.TextColor is string textHex && !IsHexColor(textHex))
            {
                return Fail("invalid text color: " + textHex + " (#rrggbb)");
            }

            if (args.FontSize is double points)
            {
                if (update)
                {
                    return Fail("session.hud.update: --font-size is fixed at open; reopen the hud to change it");
                }

                if (points < HudSpec.MinFontSize || points > HudSpec.MaxFontSize || double.IsNaN(points))
                {
                    return Fail("session.hud.open: --font-size must be " + HudSpec.MinFontSize + "..." + HudSpec.MaxFontSize + " points");
                }
            }

            if (args.SizePercent is int requested && requested is < 1 or > 100)
            {
                return Fail(command + ": --size-percent must be 1...100");
            }

            if (args.HideAfter is double seconds && (seconds < 0 || seconds > HudSpec.MaxHideAfter || double.IsNaN(seconds)))
            {
                return Fail(command + ": --hide-after must be 0..." + HudSpec.MaxHideAfter + " seconds");
            }

            string position = HudPositions.Normalize(args.Position)
                ?? throw new ControlException("invalid position: " + args.Position + " (" + string.Join("|", HudPositions.All.Concat(new[] { "top", "bottom" })) + ")");
            string? spinner = args.Spinner switch
            {
                null or HudSpinners.None => null,
                string style when HudSpinners.All.Contains(style) => style,
                _ => throw new ControlException("invalid spinner: " + args.Spinner + " (" + string.Join("|", HudSpinners.All) + "|none)"),
            };

            // agterm's one HUD per session, in the session-wide slot: --pane or --pane-id only says which pane it is drawn
            // over, and an update without them returns it over the whole session. An open needs that pane on screen.
            Session session = ResolveSession(request.Target);
            PaneRole? anchor = PaneById(session, args, alwaysFallBack: false) ?? named;
            SessionOverlay? current = session.Overlay;

            // agterm answers a hidden slot, and an update with no HUD, before it looks at the pane.
            if (!update && current is { Hidden: true })
            {
                return Fail("overlay hidden");
            }

            if (update && current is not { IsHud: true })
            {
                return Fail("no hud");
            }

            if (anchor == PaneRole.Scratch)
            {
                return Fail("hud pane must be left or right");
            }

            // agterm's resolvePanePlacement: an open needs the pane on screen, an update only that it exists.
            if (anchor is PaneRole placed && !update && !session.RendersPane(placed))
            {
                return Fail("pane not visible");
            }

            if (anchor == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split");
            }

            // A program or page holds the slot: as agterm's, a HUD does not end it.
            if (!update && current is { IsHud: false })
            {
                return Fail("overlay already open");
            }

            // Everything is checked before anything changes: a refused call leaves the panel as it was.
            int? percent = ValidPercent(args.SizePercent, command);
            string? textColor = ValidColor(args.TextColor);
            string? background = update ? current!.BackgroundColor : ValidColor(args.Color);
            HudSpec hud = new HudSpec
            {
                Message = args.Message,
                Detail = args.Detail,
                Spinner = spinner,
                Position = position,
                TextColor = textColor,
                // As agterm's, the font size is the panel's for its life: an update keeps the one it opened with.
                FontSize = update ? current!.Hud!.FontSize : args.FontSize,
                Sticky = args.Sticky == true,
                Frame = args.Frame != false,
                HideAfter = args.HideAfter > 0 ? args.HideAfter : null,
                Markdown = markdown,
            };

            // An update repaints the same panel; an open puts up a new one in place of the session's earlier HUD.
            string id = update ? current!.Id : Ids.New();
            model.OpenOverlay(session.Id, new SessionOverlay
            {
                Id = id,
                Hud = hud,
                HudPane = anchor,
                ShownAt = DateTimeOffset.UtcNow,
                SizePercent = percent is int width ? HudSpec.ClampWidth(width, hud) : null,
                // The background is read once when the panel is made: an update keeps it, as agterm's.
                BackgroundColor = background,
            });
            // An update replaces the panel's timer, and one without --hide-after keeps it up.
            host.ScheduleHudHide(session.Id, id, hud.HideAfter);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>
        /// agterm: on the machine that attached a session, a pane another machine leads is covered and refuses scripts until it
        /// leads again. On the session's own machine type, text and cursor go through the session itself and keep working;
        /// paste, copy, select-all and search act on the covered terminal, so they are refused there too (<paramref name="hereToo"/>).
        /// </summary>
        /// <summary>
        /// agterm's paneSource: a live pane of this machine whose daemon reported a role reads from the daemon's terminal,
        /// which has the leader's layout, while what it shows may be laid out for another grid; only the default read of a
        /// pane that is not covered, its own viewport, stays on the pane. A remote pane's daemon is out of reach, so it
        /// reads its own terminal (RequireUncovered refuses one that is covered).
        /// </summary>
        private bool FromDaemon(Session session, PaneRole pane, bool viewport = false)
        {
            if (session.RemoteHost is not null || pane is not (PaneRole.Main or PaneRole.Split) || (host.PaneLead(session.Id, pane) is null && !host.PaneCovered(session.Id, pane)))
            {
                return false;
            }

            // agterm's ZmxLeadState.covered: a follower draws for another grid, an unowned pane for nobody's.
            return host.PaneCovered(session.Id, pane) || !viewport;
        }

        private void RequireUncovered(Session session, PaneRole pane, bool hereToo = false)
        {
            if ((session.RemoteHost is not null || hereToo) && pane is PaneRole.Main or PaneRole.Split && host.PaneCovered(session.Id, pane))
            {
                // agterm's two refusals: a read or a keystroke of an attached pane, and an action on the covered terminal itself.
                throw new ControlException(hereToo
                    ? "pane is covered while another machine leads it; take the lead first (session lead)"
                    : "pane is in use on the machine it runs on; take the lead to drive it from here");
            }
        }

        private ControlResponse TakeLead(ControlRequest request)
        {
            if (!Panes.TryParse(request.Args?.Pane ?? "left", out PaneRole pane) || pane is not (PaneRole.Main or PaneRole.Split or PaneRole.Scratch))
            {
                return Fail("invalid pane: " + request.Args?.Pane);
            }

            if (pane == PaneRole.Scratch)
            {
                return Fail("the scratch terminal has no lead");
            }

            Session session = ResolveSession(request.Target);

            if (pane == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split pane");
            }

            return host.TakeLead(session.Id, pane) is string error ? Fail(error) : Ok(new ControlResult { Id = session.Id.Wire() });
        }

        /// <summary>agterm's listZmxDaemons: a listing that failed is an error rather than an empty inventory.</summary>
        private async Task<ControlResponse> LiveListAsync()
        {
            (ControlLiveInventory? inventory, string? error) = await host.LiveListAsync();
            return inventory is null ? Fail(error ?? "could not read the live session list") : Ok(new ControlResult { Live = inventory });
        }

        private async Task<ControlResponse> LivePruneAsync()
        {
            (string? error, string report, int pruned) = await host.LivePruneAsync();
            return error is null ? Ok(new ControlResult { Text = report, Affected = pruned }) : Fail(error);
        }

        private ControlResponse LiveAnswer(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            if (request.Args?.Presenter is not string presenter || request.Args.Answer is not ControlPresentationFrame answer)
            {
                return Fail("live.answer requires a presenter and an answer");
            }

            // agterm logs why a presenter refused a page: the open was answered ok, and the refusal surfaces nowhere else. A
            // presenter from before the reason sends none.
            if (answer.Kind == ControlPresentationKinds.PageRejected)
            {
                Log.Notice("PageServe", "presenter refused page " + answer.Id + ": " + (answer.Reason ?? "no reason"));
            }

            bool taken = answer.Kind switch
            {
                ControlPresentationKinds.OverlayRejected => model.RejectOverlayJob(session.Id, presenter, answer),
                ControlPresentationKinds.OverlayClosed => model.OverlaySurfaceClosed(session.Id, presenter, answer),
                ControlPresentationKinds.PageRejected or ControlPresentationKinds.PageClosed => model.RemotePageGone(session.Id, presenter, answer),
                ControlPresentationKinds.PageState => model.RemotePageState(session.Id, presenter, answer),
                _ => model.AnswerRemoteAsk(session.Id, presenter, answer),
            };
            return taken ? Ok(new ControlResult()) : Fail("not handed to this presenter");
        }

        private ControlResponse LivePresent(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            if (session.RemoteHost is not null)
            {
                return Fail("session is shown from " + session.RemoteHost + "; present it there");
            }

            if (request.Args?.Mode == "release" && request.Args.Presenter is string leaving)
            {
                model.ReleasePresenter(session.Id, leaving);
                return Ok(new ControlResult());
            }

            // agterm refuses a session whose panes are not all live when the stream starts; once it runs, a split still
            // starting or a shell between lives does not end it.
            bool acquire = request.Args?.Mode == "acquire";
            // agterm's hello: what the stream speaks decides what is handed to it once it presents.
            if (request.Args?.Kinds is List<string> kinds && request.Args.Presenter is string speaking)
            {
                model.SetStreamKinds(speaking, kinds);
            }

            bool connecting = request.Args?.Presenter is not string token || !model.IsPresentationReader(session.Id, token);
            if (connecting && (host.PaneLead(session.Id, PaneRole.Main) is null || (session.HasSplit && host.PaneLead(session.Id, PaneRole.Split) is null)))
            {
                return Fail("session is not live-backed, so nothing can be attached to it");
            }

            return Ok(new ControlResult { Presentation = model.Presentation(session.Id, DateTimeOffset.UtcNow, request.Args?.Presenter, acquire) });
        }

        /// <summary>agterm's hud close: the session's one HUD, whichever pane it is drawn over; a program is left alone.</summary>
        private ControlResponse CloseHud(ControlRequest request)
        {
            Session session = ResolveSession(request.Target);
            if (session.Overlay is not { IsHud: true } hud)
            {
                return Fail("no hud");
            }

            model.CloseOverlay(session.Id, hud.Id);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private static int? ValidPercent(int? percent, string command)
        {
            return percent is null or (>= 1 and <= 100) ? percent : throw new ControlException(command + ": --size-percent must be 1...100");
        }

        /// <summary>agterm's isValidColorHex: six hex digits, the leading # optional.</summary>
        private static bool IsHexColor(string color)
        {
            string hex = color.StartsWith('#') ? color[1..] : color;
            return hex.Length == 6 && hex.All(Uri.IsHexDigit);
        }

        private static string? ValidColor(string? color)
        {
            bool valid = color is null || IsHexColor(color);
            return valid ? color : throw new ControlException("invalid color: " + color + " (#rrggbb)");
        }

        /// <summary>The pane a command addresses; a split or scratch that has no shell is an error.</summary>
        private PaneRole ResolvePane(Session session, string? value, PaneRole fallback)
        {
            PaneRole pane = fallback;
            if (value is not null && !Panes.TryParse(value, out pane))
            {
                throw new ControlException("invalid pane: " + value);
            }

            if (pane != PaneRole.Main && !host.IsRunning(session.Id, pane) && !host.IsHeld(session.Id, pane))
            {
                throw new ControlException(pane == PaneRole.Scratch ? "session has no scratch terminal" : "session has no split pane");
            }

            return pane;
        }

        private static bool? ParseToggle(string? mode, string on, string off, string noun)
        {
            if (mode is null || mode == "toggle")
            {
                return null;
            }

            if (mode == on)
            {
                return true;
            }

            return mode == off ? false : throw new ControlException("invalid " + noun + " mode: " + mode);
        }

        private ControlResponse SetStatus(ControlRequest request)
        {
            // agterm's order: status, color, shape, pane, then the sound, all before the session.
            if (!AgentStatuses.TryParse(request.Args?.Status, out AgentStatus status))
            {
                return Fail("invalid status");
            }

            if (request.Args?.Color is string statusColor && !IsHexColor(statusColor))
            {
                return Fail("invalid color (expected #rrggbb)");
            }

            string? shape = request.Args?.Shape;
            if (shape is not null && !AgentStatuses.Shapes.Contains(shape))
            {
                return Fail("invalid shape: " + shape + " (" + string.Join("|", AgentStatuses.Shapes) + ")");
            }

            PaneRole pane = PaneRole.Main;
            if (request.Args?.Pane is string value && (!Panes.TryParse(value, out pane)))
            {
                return Fail("--pane must be left, right, or scratch");
            }

            // agterm validates a named sound up front, so an unknown one leaves the status as it was.
            if (request.Args?.Sound is { Length: > 0 } named && host.SoundError(named) is string soundError)
            {
                return Fail(soundError);
            }

            Session session = ResolveSession(request.Target);
            // The hook sends the pane id, so a status lands on the terminal's pane now, after a swap or a promoted split.
            PaneRole? byId = PaneById(session, request.Args, alwaysFallBack: true);
            pane = byId ?? pane;
            bool paneNamed = byId is not null || request.Args?.Pane is not null;

            string? color = request.Args?.Color;

            bool wasBlocked = session.Status == AgentStatus.Blocked;
            if (!model.SetStatus(session.Id, status, request.Args?.Blink == true, request.Args?.AutoReset == true, pane, color: color, shape: shape, paneNamed: paneNamed))
            {
                return Fail("blocked status owned by pane " + Panes.Name(session.StatusPane) + " (write from that pane to change it)");
            }

            // agterm: while another machine leads the pane that set the status, its presenter plays the sound instead.
            // As agterm's: a named sound plays with the write, and else, an empty name included, the blocked sound on entering it.
            string? sound = request.Args?.Sound is { Length: > 0 } perCall ? perCall : null;
            bool plays = sound is not null || (status == AgentStatus.Blocked && !wasBlocked);
            string? lead = session.StatusPane is PaneRole.Main or PaneRole.Split ? host.PaneLead(session.Id, session.StatusPane) : null;
            if (plays && (lead != "follower" || !model.SendSoundToPresenter(session.Id, sound)))
            {
                host.StatusSound(status, sound);
            }

            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private ControlResponse Notify(ControlRequest request)
        {
            // agterm: only an empty body is refused, and before the target is looked up.
            string? body = request.Args?.Body;
            if (string.IsNullOrEmpty(body))
            {
                return Fail("notify requires a body");
            }

            Session session = ResolveSession(request.Target);
            // agterm: only an empty title takes the session's name; one of spaces is a title.
            string title = string.IsNullOrEmpty(request.Args?.Title) ? session.DisplayName : request.Args!.Title!;
            model.RecordNotification(session.Id, title, body, NotificationSources.Control);
            // agterm says so when the badge counts but no banner shows.
            return host.Notify(session.Id, title, body)
                ? Ok(new ControlResult { Id = session.Id.Wire() })
                : Ok(new ControlResult { Id = session.Id.Wire(), Text = "badge updated, but \"Show notification banners\" is off, so no banner was posted" });
        }

        private ControlResponse ReadEvents(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if ((args.Run is null) != (args.After is null))
            {
                return Fail("events.read requires --run and --after together");
            }

            long? after = null;
            if (args.After is not null)
            {
                if (!long.TryParse(args.After, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long cursor))
                {
                    return Fail("invalid event cursor");
                }

                after = cursor;
            }

            int limit = args.Limit ?? 100;
            if (limit < 1 || limit > 1000)
            {
                return Fail("event limit must be between 1 and 1000");
            }

            HashSet<string>? kinds = null;
            if (args.Kinds is { Count: > 0 })
            {
                string? unknown = args.Kinds.FirstOrDefault(kind => !ControlEventKinds.All.Contains(kind));
                if (unknown is not null)
                {
                    return Fail("invalid event kind: " + unknown);
                }

                kinds = args.Kinds.ToHashSet();
            }

            (ControlEventBatch batch, string? error) = model.Events.Read(args.Run, after, kinds, limit);
            return error is null
                ? Ok(new ControlResult { Events = batch })
                : new ControlResponse { Ok = false, Error = error, Result = new ControlResult { Events = batch } };
        }

        private ControlResponse ReloadConfig(bool keymapFile)
        {
            IReadOnlyList<string> diagnostics = host.ReloadConfig(keymapFile, !keymapFile);
            // agterm: the count, printed as ok or N diagnostic(s); keymap list and hooks list show them.
            return Ok(new ControlResult { Count = diagnostics.Count });
        }

        public static ControlRecentItem RecentNode(RecentClosedItem item)
        {
            return new ControlRecentItem
            {
                Id = item.Id.Wire(),
                Kind = item.Kind,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ClosedAt = item.ClosedAt.ToUnixTimeMilliseconds() / 1000.0,
                WorkspaceId = item.WorkspaceId?.Wire() ?? string.Empty,
                SessionIds = item.SessionIds.Select(id => id.Wire()).ToList(),
            };
        }

        /// <summary>agterm's dispatchRecentCommand: recent.list and recent.clear are app-wide, so a target or --window is refused.</summary>
        public static string? RecentRefusal(ControlRequest request)
        {
            return request.Cmd is "recent.list" or "recent.clear" && (request.Target is not null || request.Args?.Window is not null)
                ? request.Cmd + " takes no target or --window"
                : null;
        }

        public static string? HooksRefusal(ControlRequest request)
        {
            return request.Cmd is "hooks.list" or "hooks.reload" && (request.Target is not null || request.Args?.Window is not null)
                ? request.Cmd + " takes no target or --window"
                : null;
        }

        /// <summary>Opens the newest entry, or the one whose id matches exactly or by a unique prefix.</summary>
        private ControlResponse OpenRecent(ControlRequest request)
        {
            (RecentClosedItem? item, string? error) = model.Recent.Resolve(request.Target);
            if (item is null)
            {
                return Fail(error!);
            }

            ControlRecentItem reopened = RecentNode(item);
            RecentClosedRestore outcome = model.ReopenRecent(item, new HashSet<Guid>());
            return outcome.Restored
                ? Ok(new ControlResult { RecentOpen = new ControlRecentOpen { Item = reopened, Complete = outcome.Complete } })
                : Fail("could not reopen " + item.Id.Wire());
        }

        private ControlResponse ClearRecent()
        {
            return model.ClearRecent() ? Ok() : Fail("could not clear recent items");
        }

        private ControlResponse RestoreMode(ControlRequest request)
        {
            (string? error, ControlRestoreStatus status) = host.RestoreMode(request.Args?.Mode);
            return error is null ? Ok(new ControlResult { Restore = status }) : Fail(error);
        }

        private ControlResponse SetTheme(ControlRequest request)
        {
            // agterm's ThemeCatalog.resolvedName: Swift's .whitespaces trimmed (tab and the space separators, not line breaks),
            // and empty counts as not given.
            ControlArgs args = request.Args ?? new ControlArgs();
            static bool Blank(char c) => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;
            static string? Resolved(string? value)
            {
                string trimmed = value is null ? string.Empty : new string(value.SkipWhile(Blank).Reverse().SkipWhile(Blank).Reverse().ToArray());
                return trimmed.Length > 0 ? trimmed : null;
            }
            string? name = Resolved(args.Name);
            string? light = Resolved(args.Light);
            if (name is not null && light is not null)
            {
                return Fail("theme.set takes either a name or --light, not both");
            }

            string? error = host.SetTheme(light ?? name, Resolved(args.Dark));
            return error is null ? Ok(ThemeResult(withNames: false)) : Fail(error);
        }

        /// <summary>agterm's theme read-back: the theme in force, whether it follows Windows and its pair, the list for theme.list.</summary>
        private ControlResult ThemeResult(bool withNames)
        {
            (IReadOnlyList<string> names, string? current, bool sync, string? light, string? dark) = host.ThemeState();
            return new ControlResult { Theme = current, Themes = withNames ? names.ToList() : null, Sync = sync, Light = light, Dark = dark };
        }

        /// <summary>agterm's keymap.run: the command by name first, then the session it runs for, which it needs.</summary>
        private async Task<ControlResponse> RunCustomAsync(ControlRequest request)
        {
            // agterm matches the name exactly: one of spaces is a name too.
            string? name = request.Args?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return Fail("keymap.run requires a command name");
            }

            if (!host.HasCustomCommand(name))
            {
                return Fail("no custom command named " + name);
            }

            Session session = ResolveSession(request.Target);
            return await host.RunCustomCommandAsync(name, session.Id) is string error ? Fail(error) : Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private static string DashboardMember(DashboardCell cell)
        {
            return cell.SessionId.Wire() + ":" + (cell.Pane == PaneRole.Split ? "right" : "left");
        }

        /// <summary>
        /// Opens the dashboard on named sessions (a :left or :right suffix places one pane), or on the most recently used ones,
        /// or closes it. As in agterm, the nine-cell cap counts panes and what does not fit is reported.
        /// </summary>
        private ControlResponse Dashboard(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            List<string> ids = args.Targets ?? new List<string>();
            if (args.Close == true)
            {
                if (ids.Count > 0 || args.Mru == true || args.FontSize is not null || args.AutoSize == true)
                {
                    return Fail("dashboard --close takes no ids, --mru, or font options");
                }

                model.CloseDashboard();
                return Ok();
            }

            if (args.FontSize is not null && args.AutoSize == true)
            {
                return Fail("dashboard: --font-size is mutually exclusive with --auto-size");
            }

            if (args.FontSize is double size && (size <= 0 || !double.IsFinite(size)))
            {
                return Fail("dashboard --font-size must be a positive number");
            }

            if (args.Mru == true && ids.Count > 0)
            {
                return Fail("dashboard --mru cannot be combined with explicit session ids");
            }

            if (args.Mru != true && ids.Count == 0)
            {
                return Fail("dashboard requires at least one session id");
            }

            // agterm: the grammar of every target is checked before anything else looks at the window.
            List<DashboardTarget> parsed = new List<DashboardTarget>();
            foreach (string id in ids)
            {
                if (DashboardTarget.Parse(id) is not DashboardTarget target)
                {
                    return Fail("dashboard: invalid session id '" + id + "' — use <id>, <id>:left, or <id>:right");
                }

                parsed.Add(target);
            }

            if (model.PendingModalError is string modal)
            {
                return Fail(modal);
            }

            // agterm: an id that resolves to nothing, or :right on a session with no split, is a miss reported back, not a failure.
            List<(Session, PaneRole?)> targets = new List<(Session, PaneRole?)>();
            List<string> unresolved = new List<string>();
            if (args.Mru == true)
            {
                targets.AddRange(model.RecentSessions.Select(model.FindSession).OfType<Session>().Take(DashboardLayout.MaxCells).Select(session => (session, (PaneRole?)null)));
                if (targets.Count == 0)
                {
                    return Fail("no recent sessions");
                }
            }

            foreach ((string id, DashboardTarget target) in ids.Zip(parsed))
            {
                PaneRole? pane = target.Pane;
                Session? session;
                try
                {
                    session = ResolveSession(target.Head);
                }
                catch (ControlException)
                {
                    session = null;
                }

                if (session is null || (pane == PaneRole.Split && !session.HasSplit))
                {
                    unresolved.Add(id);
                    continue;
                }

                targets.Add((session, pane));
            }

            (List<DashboardCell> cells, int dropped) = model.DashboardCells(targets);
            if (cells.Count == 0)
            {
                return Fail("no dashboard sessions resolved");
            }

            model.OpenDashboard(cells, args.FontSize, args.AutoSize == true);
            List<string> notes = new List<string>();
            if (unresolved.Count > 0)
            {
                notes.Add("unresolved: " + string.Join(", ", unresolved));
            }

            if (dropped > 0)
            {
                notes.Add("dropped " + dropped + " pane(s) beyond the " + DashboardLayout.MaxCells + "-cell limit");
            }

            return notes.Count == 0 ? Ok() : Ok(new ControlResult { Text = string.Join("; ", notes) });
        }

        private async Task<ControlResponse> ClearBrowserAsync()
        {
            return await host.ClearBrowserDataAsync() is string failure ? Fail("browser.clear: " + failure) : Ok();
        }

        private ControlResponse Sidebar(ControlRequest request)
        {
            bool visible = request.Args?.Mode switch
            {
                "show" => true,
                "hide" => false,
                null or "toggle" => !host.SidebarVisible,
                _ => throw new ControlException("invalid sidebar mode: " + request.Args?.Mode),
            };

            host.SetSidebar(visible, null);
            return Ok();
        }

        private ControlResponse SidebarWidth(ControlRequest request)
        {
            if (request.Args?.SidebarWidth is not double width || !double.IsFinite(width))
            {
                return Fail("sidebar.width requires a width in points");
            }

            host.SetSidebar(null, width);
            return Ok(new ControlResult { SidebarWidth = host.SidebarWidth });
        }

        private ControlResponse WithWorkspace(ControlRequest request, Action<Workspace> action)
        {
            Workspace workspace = ResolveWorkspace(request.Target);
            action(workspace);
            return Ok(new ControlResult { Id = workspace.Id.Wire() });
        }

        private ControlResponse WithSession(ControlRequest request, Action<Session> action)
        {
            Session session = ResolveSession(request.Target);
            action(session);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }

        private Workspace ResolveWorkspace(string? target)
        {
            string value = target ?? ControlTargets.Active;
            TargetResolution resolution = ControlTargets.Resolve(value, model.Workspaces.Select(workspace => workspace.Id), model.CurrentWorkspace?.Id);
            if (resolution.Match != TargetMatch.Resolved)
            {
                throw new ControlException(ControlTargets.ErrorMessage("workspace", value, resolution));
            }

            return model.FindWorkspace(resolution.Id)!;
        }

        private Session ResolveSession(string? target)
        {
            string value = target ?? ControlTargets.Active;
            TargetResolution resolution = ControlTargets.Resolve(value, model.AllSessions.Select(session => session.Id), model.SelectedSessionId);
            if (resolution.Match != TargetMatch.Resolved)
            {
                throw new ControlException(ControlTargets.ErrorMessage("session", value, resolution));
            }

            return model.FindSession(resolution.Id)!;
        }

        private List<Session> ResolveSessions(ControlRequest request)
        {
            if (request.Args?.Targets is { Count: 0 })
            {
                throw new ControlException("session command requires at least one --target");
            }

            IEnumerable<string?> targets = request.Args?.Targets is { Count: > 0 } list ? list : new[] { request.Target };
            return targets.Select(ResolveSession).Distinct().ToList();
        }

        private static int Reorder(string? direction, int index, int count, string command)
        {
            return direction switch
            {
                "up" => Math.Max(0, index - 1),
                "down" => Math.Min(count - 1, index + 1),
                "top" => 0,
                "bottom" => count - 1,
                null => throw new ControlException(command + " requires --to"),
                _ => throw new ControlException(command + " --to must be up|down|top|bottom"),
            };
        }

        /// <summary>agterm's answer to a cmd its Command enum does not decode: the decoder's own words.</summary>
        public static string UnknownCommand(string cmd)
        {
            return "invalid request: Cannot initialize Command from invalid String value " + cmd;
        }

        private static ControlResponse Ok(ControlResult? result = null)
        {
            return ControlResponse.Success(result);
        }

        private static ControlResponse Fail(string error)
        {
            return ControlResponse.Failure(error);
        }

        /// <summary>A refusal that still names what it is about, as overlay result's id for a poll telling its overlay apart.</summary>
        private static ControlResponse Fail(string error, ControlResult result)
        {
            return ControlResponse.Failure(error) with { Result = result };
        }

    }

    /// <summary>A refusal raised where the request is handled, the host included; the dispatcher answers it as an error.</summary>
    public sealed class ControlException : Exception
    {
        public ControlException(string message)
            : base(message)
        {
        }
    }
}
