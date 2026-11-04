using System.IO;
using System.Windows;
using AgShell.Core;
using AgShell.Pty;
using Microsoft.Win32;

namespace AgShell.App
{
    /// <summary>Folders and files dragged in from Explorer, and Open Directory.</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Folders dropped on the sidebar open as sessions, as agterm imports them: in the workspace of the row they landed on,
        /// else in the one the filter narrows to, else in the current one. Files are ignored; so is the flagged view, which has
        /// no workspaces to land in. More folders than agterm's limit open none and say so.
        /// </summary>
        private void DropFolders(IEnumerable<string> paths, Guid? workspaceId, Guid? springOpen)
        {
            List<string> folders = new List<string>();
            bool exceedsLimit = false;
            foreach (string folder in paths.Where(Directory.Exists))
            {
                if (folders.Count == SidebarDrop.MaximumDirectoryImportCount)
                {
                    exceedsLimit = true;
                    break;
                }

                folders.Add(folder);
            }

            Guid? row = workspaceId is Guid id && model.FindWorkspace(id) is not null ? id : null;
            if (folders.Count == 0 || model.SidebarMode != AppModel.TreeMode)
            {
                return;
            }

            if (exceedsLimit)
            {
                MessageBox.Show(this, Strings.T("Слишком много папок") + "\n\n" + Strings.T("За раз можно открыть не больше {0} папок.", SidebarDrop.MaximumDirectoryImportCount), "AgShell", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Guid target = SidebarDrop.ResolveDirectoryWorkspace(model.SidebarMode, row, model.SoleFocusedWorkspaceId, model.CurrentWorkspace?.Id) ?? model.AddWorkspace().Id;
            ShellProfile profile = DefaultProfile();
            for (int i = 0; i < folders.Count; i++)
            {
                model.AddSession(target, profile.Id, folders[i], select: i == folders.Count - 1);
            }

            if (springOpen is Guid sprung)
            {
                model.KeepSpringOpen(sprung);
            }
        }

        /// <summary>
        /// agterm's validateDrop answers no to a drop that changes nothing, so it is not highlighted. The page cannot ask on each
        /// move, so it is told at the start of a drag which places are no-ops: a workspace row, a session row's upper or lower
        /// half, a slot between shown workspaces.
        /// </summary>
        private void PostDropPlan(int token, List<Guid>? sessionIds, Guid? workspaceId)
        {
            List<string> noop = new List<string>();
            if (sessionIds is { Count: > 0 })
            {
                foreach (Workspace workspace in model.VisibleWorkspaces)
                {
                    if (model.ResolveSessionDrop(sessionIds, workspace.Id, null, after: true) is null)
                    {
                        noop.Add(workspace.Id.ToString());
                    }

                    foreach (Session session in workspace.Sessions)
                    {
                        if (model.ResolveSessionDrop(sessionIds, workspace.Id, session.Id, after: false) is null)
                        {
                            noop.Add(session.Id + ":before");
                        }

                        if (model.ResolveSessionDrop(sessionIds, workspace.Id, session.Id, after: true) is null)
                        {
                            noop.Add(session.Id + ":after");
                        }
                    }
                }
            }
            else if (workspaceId is Guid dragged)
            {
                int shown = model.VisibleWorkspaces.Count();
                for (int slot = 0; slot <= shown; slot++)
                {
                    if (model.ResolveWorkspaceDrop(dragged, slot) is null)
                    {
                        noop.Add("slot:" + slot);
                    }
                }
            }

            Post(new { type = "dropPlan", token, noop });
        }

        /// <summary>Paths dropped on a terminal are typed into it, quoted for its shell, as Ghostty pastes them.</summary>
        private void DropPaths(PaneKey key, IEnumerable<string> paths)
        {
            string text = QuotedPaths(key, paths);
            if (text.Length > 0)
            {
                Post(new { type = "paste", sessionId = key.SessionId, pane = Panes.Name(key.Pane), text });
            }
        }

        /// <summary>
        /// agterm's drop of text: links go in quoted for the pane's shell, a local file:// link as its path, and plain text
        /// as it is, all as a paste.
        /// </summary>
        private void DropText(PaneKey key, string text, IReadOnlyList<string> uris)
        {
            List<string> links = uris.Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile ? parsed.LocalPath : uri).ToList();
            string pasted = links.Count > 0 ? QuotedPaths(key, links) : text;
            if (pasted.Length > 0)
            {
                Post(new { type = "paste", sessionId = key.SessionId, pane = Panes.Name(key.Pane), text = pasted });
            }
        }

        /// <summary>Paths for the pane's shell, escaped for it and joined by spaces, as agterm's pasteboardText escapes file URLs.</summary>
        private string QuotedPaths(PaneKey key, IEnumerable<string> paths)
        {
            Session? session = key.Pane == PaneRole.Quick && model.SelectedSessionId is Guid selected ? model.FindSession(selected) : model.FindSession(key.SessionId);
            ShellKind kind = session is null ? DefaultProfile().Kind : ProfileOrDefault(session.ProfileId).Kind;
            return string.Join(" ", paths.Select(path => kind switch
            {
                ShellKind.Wsl => PastedPath.ForPosix(PastedPath.WslPath(path)),
                ShellKind.Cmd => PastedPath.ForCmd(path),
                _ => PastedPath.ForPowerShell(path),
            }));
        }

        /// <summary>
        /// agterm's pasteboardText, what a paste or a program's clipboard read gets: files copied in Explorer as their paths
        /// for the pane's shell, otherwise the text as it is; null when the clipboard holds neither.
        /// </summary>
        internal string? ClipboardTextFor(PaneKey key)
        {
            try
            {
                if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
                {
                    return QuotedPaths(key, files.Cast<string>());
                }

                return Clipboard.ContainsText() ? Clipboard.GetText() is { Length: > 0 } text ? text : null : null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        /// <summary>agterm's Open Directory…: a new session rooted in a folder the user picks.</summary>
        private void OpenDirectory(Guid? workspaceId)
        {
            // agterm opens the picker in the focused pane's directory.
            OpenFolderDialog dialog = new OpenFolderDialog { Title = Strings.T("Каталог для новой сессии"), InitialDirectory = DirectoryPanelDefaults.Folder(ActiveSessionDirectory) };
            if (dialog.ShowDialog(this) != true || !Directory.Exists(dialog.FolderName))
            {
                return;
            }

            // agterm: the row's workspace or the current one, in any sidebar mode; not the drop's choice by the focus filter.
            Workspace workspace = (workspaceId is Guid id ? model.FindWorkspace(id) : null)
                ?? model.CurrentWorkspace
                ?? model.AddWorkspace();
            model.AddSession(workspace.Id, DefaultProfile().Id, dialog.FolderName);
        }
    }
}
