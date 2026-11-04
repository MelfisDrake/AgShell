using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>Closing from the window with agterm's undo grace, and the recently closed list.</summary>
    public partial class MainWindow
    {
        private void InitClosing()
        {
            // agterm keeps one recently closed list for the app; every window adds to it.
            model.ShareRecent(Library.Recent);
            model.ShareAskResults(Library.AskResults);
            Library.Recent.Changed += OnRecentChanged;
            Closing += (sender, args) =>
            {
                foreach (Session session in model.FinalizeAllCloses())
                {
                    StopSession(session.Id);
                }

                // agterm's willClose: the window's picker and questions are cancelled, and the app keeps their answers.
                Library.TrackClosingModals(this);
                model.CancelAllModals();
                Library.KeepModalResults(Entry, model);
                // agterm's closeWindow: the window's sessions leave the app with it, and are reported closed (not while
                // the app itself is being ended, as agterm's closeWindow does nothing then).
                if (!endedForDelete && !Library.Terminating)
                {
                    model.AnnounceDeleted();
                }
            };
        }

        /// <summary>
        /// The WPF WebView2 control answers a page's window.close() by closing the window it sits in, which is this one; the
        /// page closes only its overlay (NativePages.CloseRequested), so that close is refused before anything runs.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "CoreWebView2_WindowCloseRequested"))
            {
                e.Cancel = true;
                return;
            }

            if (!closeAnswered && !Library.ShuttingDown && !Library.Terminating && model.AllSessions.Count() is int count and > 0)
            {
                e.Cancel = true;
                AskClose(count);
                return;
            }

            base.OnClosing(e);
        }

        /// <summary>The sessions in the window, for the quit question.</summary>
        internal int SessionCount => model.AllSessions.Count();

        /// <summary>The user said yes to the close, or it is the app's own (window close, window delete): it is not asked again.</summary>
        private bool closeAnswered;

        private bool closePromptOpen;

        /// <summary>agterm's WindowRegistry.close: a close the app makes itself, which agterm's window.close() makes without the sheet.</summary>
        internal void CloseWithoutAsking()
        {
            closeAnswered = true;
            Close();
        }

        /// <summary>
        /// agterm's windowShouldClose: a window the user closes with sessions in it asks first, saying how many sessions go
        /// and that the window can be opened again; in live they are only detached, otherwise they end.
        /// </summary>
        private void AskClose(int count)
        {
            if (closePromptOpen)
            {
                return;
            }

            closePromptOpen = true;
            bool live = Library.RestoreStatus().Active == CommandRestore.Live;
            string text = (live, count == 1) switch
            {
                (true, true) => Strings.T("Закроется 1 сессия. Окно можно снова открыть из палитры: «Открыть окно»."),
                (true, false) => Strings.T("Закроется сессий: {0}. Окно можно снова открыть из палитры: «Открыть окно».", count),
                (false, true) => Strings.T("Завершится 1 работающая сессия. Окно можно снова открыть из палитры: «Открыть окно»."),
                (false, false) => Strings.T("Завершится работающих сессий: {0}. Окно можно снова открыть из палитры: «Открыть окно».", count),
            };
            WindowClosePrompt.Show(this, Strings.T("Закрыть «{0}»?", Library.Label(entry)), text, close =>
            {
                closePromptOpen = false;
                if (close)
                {
                    CloseWithoutAsking();
                }
            });
        }

        /// <summary>
        /// Closes sessions the user closed in the window. With the undo grace on, as agterm by default, they leave the tree
        /// at once but their shells live for a few seconds, so Ctrl+Shift+Z brings them back as they were.
        /// </summary>
        private void CloseSessionsFromWindow(IReadOnlyList<Guid> sessionIds)
        {
            if (settings.CloseGraceUndoEnabled == false)
            {
                foreach (Guid id in sessionIds.Where(id => model.FindSession(id) is not null))
                {
                    CloseSession(id);
                }

                return;
            }

            ScheduleFinalize(model.SoftCloseSessions(sessionIds));
        }

        public int CloseSessionsBatch(IReadOnlyList<Guid> sessionIds)
        {
            if (settings.CloseGraceUndoEnabled == false)
            {
                int closed = 0;
                foreach (Guid id in sessionIds.Where(id => model.FindSession(id) is not null))
                {
                    CloseSession(id);
                    closed++;
                }

                return closed;
            }

            PendingClose? close = model.SoftCloseSessions(sessionIds);
            ScheduleFinalize(close);
            return close is null ? 0 : sessionIds.Count;
        }

        private bool deletePromptOpen;

        /// <summary>
        /// agterm's deleteWorkspace from the window: a workspace still holding sessions asks first, whatever the close
        /// confirmation setting, since the delete ends their shells; an empty one goes at once, the last one stays.
        /// </summary>
        private void DeleteWorkspaceFromWindow(Guid workspaceId)
        {
            if (!model.CanRemoveWorkspace || model.FindWorkspace(workspaceId) is not Workspace workspace)
            {
                return;
            }

            ConfirmDelete(workspace.Name, workspace.Sessions.Count, () =>
            {
                if (model.CanRemoveWorkspace && model.FindWorkspace(workspaceId) is not null)
                {
                    CloseWorkspaceFromWindow(workspaceId);
                }
            });
        }

        /// <summary>agterm's deleteActiveWindow: the window asks only while it still has sessions, then is deleted as by window.delete.</summary>
        private void DeleteWindowFromWindow()
        {
            ConfirmDelete(Library.Label(entry), model.AllSessions.Count(), () => Library.RunWindowCommand("window.delete", entry.Id.Wire()));
        }

        /// <summary>agterm's confirmDelete: "Delete “name”?", how many sessions it closes and how they can come back.</summary>
        private void ConfirmDelete(string name, int sessionCount, Action delete)
        {
            if (sessionCount == 0)
            {
                delete();
                return;
            }

            if (deletePromptOpen)
            {
                return;
            }

            deletePromptOpen = true;
            bool grace = settings.CloseGraceUndoEnabled != false;
            string text = (grace, sessionCount == 1) switch
            {
                (true, true) => Strings.T("Его сессия закроется после короткого окна для отмены."),
                (true, false) => Strings.T("Закроется сессий: {0} — после короткого окна для отмены.", sessionCount),
                (false, true) => Strings.T("Его сессия закроется сразу. Его можно снова открыть из меню «Файл ▸ Недавно закрытые»."),
                (false, false) => Strings.T("Сразу закроется сессий: {0}. Его можно снова открыть из меню «Файл ▸ Недавно закрытые».", sessionCount),
            };
            WindowClosePrompt.Show(this, Strings.T("Удалить «{0}»?", name), text, confirmed =>
            {
                deletePromptOpen = false;
                if (confirmed)
                {
                    delete();
                }
            }, Strings.T("Удалить"));
        }

        private void CloseWorkspaceFromWindow(Guid workspaceId)
        {
            if (settings.CloseGraceUndoEnabled == false)
            {
                foreach (Guid removed in model.RemoveWorkspace(workspaceId))
                {
                    StopSession(removed);
                }

                return;
            }

            ScheduleFinalize(model.SoftCloseWorkspace(workspaceId));
        }

        private void ScheduleFinalize(PendingClose? close)
        {
            if (close is null)
            {
                return;
            }

            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = AppModel.CloseGrace };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                foreach (Session session in model.FinalizeClose(close.Id))
                {
                    StopSession(session.Id);
                }
            };
            timer.Start();
        }

        /// <summary>agterm's Reopen Closed Item: only a close still in its grace comes back; Open Recent rebuilds the rest.</summary>
        private void UndoClose(Guid? closeId)
        {
            model.UndoClose(closeId);
        }

        private void OnRecentChanged()
        {
            PostState();
        }

        private object? PendingCloseView()
        {
            PendingClose? close = model.PendingCloses.LastOrDefault();
            return close is null ? null : new { id = close.Id, title = close.Title };
        }

        private object RecentView()
        {
            return model.RecentClosed.Select(item => new { id = item.Id, kind = item.Kind, title = item.Title, subtitle = item.Subtitle, closedAt = item.ClosedAt.ToUnixTimeMilliseconds() });
        }
    }
}
