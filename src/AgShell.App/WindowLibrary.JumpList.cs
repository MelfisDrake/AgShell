using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's Dock menu as the taskbar button's jump list: New Session, New Window, Quick Terminal and Dashboard for the
    /// front window, then its recent sessions and the sessions needing attention. A jump list item starts agshell.exe with
    /// --dock, which hands the item to the running instance; the list is saved ahead, not built when it opens, so it is
    /// rebuilt as the front window, its recency and its statuses change. An item is checked when it runs, as agterm checks
    /// an enabled one again, since an item cannot be shown disabled.
    /// </summary>
    internal sealed partial class WindowLibrary
    {
        public const string DockArgument = "--dock";

        private bool jumpListQueued;
        private string? jumpListShown;

        internal void ScheduleJumpList()
        {
            if (jumpListQueued)
            {
                return;
            }

            jumpListQueued = true;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                jumpListQueued = false;
                RefreshJumpList();
            });
        }

        private void RefreshJumpList()
        {
            if (Application.Current is not Application application)
            {
                return;
            }

            List<JumpTask> items = new List<JumpTask>();
            string executable = Environment.ProcessPath ?? string.Empty;
            JumpTask Item(string title, string? category, params string[] arguments) => new JumpTask
            {
                Title = title,
                Arguments = string.Join(' ', arguments.Select(Quote)),
                ApplicationPath = executable,
                IconResourcePath = executable,
                CustomCategory = category,
            };

            if (Active is MainWindow front)
            {
                string window = front.Entry.Id.Wire();
                items.Add(Item(Strings.T("Новая сессия"), null, DockArgument, "new-session", "--window", window));
                items.Add(Item(Strings.T("Новое окно"), null, DockArgument, "new-window"));
                items.Add(Item("Quick Terminal", null, DockArgument, "quick-terminal", "--window", window));
                items.Add(Item(Strings.T("Дашборд"), null, DockArgument, "dashboard", "--window", window, "--dashboard-open", front.DashboardOpen ? "true" : "false"));
                foreach (Session session in front.DockRecentSessions())
                {
                    items.Add(Item(front.DockTitle(session), Strings.T("Недавние сессии"), DockArgument, "session", "--window", window, "--session", session.Id.Wire()));
                }

                foreach (Session session in front.DockAttentionSessions())
                {
                    items.Add(Item(front.DockTitle(session), Strings.T("Сессии, ждущие внимания"), DockArgument, "session", "--window", window, "--session", session.Id.Wire()));
                }
            }
            else
            {
                items.Add(Item(Strings.T("Новое окно"), null, DockArgument, "new-window"));
            }

            string signature = string.Join("\n", items.Select(item => item.CustomCategory + "|" + item.Title + "|" + item.Arguments));
            if (signature == jumpListShown)
            {
                return;
            }

            jumpListShown = signature;
            // The shell shows the custom categories above the tasks, whatever their order in the list.
            JumpList list = new JumpList(items.Where(item => item.CustomCategory is not null).Concat(items.Where(item => item.CustomCategory is null)), false, false);
            JumpList.SetJumpList(application, list);
            try
            {
                list.Apply();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is UnauthorizedAccessException)
            {
                Log.Error("DockMenu", "jump list not applied: " + ex.Message);
            }
        }

        private static string Quote(string argument)
        {
            return argument.Contains(' ') ? "\"" + argument + "\"" : argument;
        }

        /// <summary>A jump list item handed over by a second launch: agterm's Dock menu action, on the window it was built for.</summary>
        internal void RunDock(IReadOnlyList<string> arguments)
        {
            string? Value(string name)
            {
                int at = arguments.ToList().IndexOf(name);
                return at >= 0 && at + 1 < arguments.Count ? arguments[at + 1] : null;
            }

            string? action = Value(DockArgument);
            if (action == "new-window")
            {
                // agterm's one app-level item: a new window, whatever modal holds the front one.
                RunWindowCommand("window.new");
                return;
            }

            // An item of a window closed since does nothing, as agterm's captured window gone.
            MainWindow? window = open.FirstOrDefault(each => string.Equals(each.Entry.Id.Wire(), Value("--window"), StringComparison.OrdinalIgnoreCase));
            if (action is null || window is null)
            {
                return;
            }

            Guid? session = Guid.TryParse(Value("--session"), out Guid id) ? id : null;
            window.RunDockAction(action, session, Value("--dashboard-open") == "true");
        }
    }
}
