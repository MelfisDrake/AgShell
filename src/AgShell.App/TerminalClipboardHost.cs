using System.IO;
using System.Text;
using System.Windows;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>A window or panel that shows clipboard prompts in its page.</summary>
    internal interface IClipboardPromptView
    {
        void ClipboardPromptsChanged();
    }

    /// <summary>
    /// agterm's ClipboardPromptController: one policy for the app's run, a remembered choice answers at once, otherwise the
    /// window holding the terminal asks. Requests of one pane in one direction wait behind one prompt, so a program looping
    /// OSC 52 cannot stack them, while another pane always gets its own.
    /// </summary>
    internal sealed class ClipboardPrompts
    {
        private readonly ClipboardPromptPolicy policy = new ClipboardPromptPolicy();
        private readonly List<Pending> pending = new List<Pending>();

        public static ClipboardPrompts Shared { get; } = new ClipboardPrompts();

        public void Request(ClipboardAccess access, IClipboardPromptView view, PaneKey requester, Action<bool> completion)
        {
            switch (policy.Decision(access))
            {
                case ClipboardDecision.Allow:
                    completion(true);
                    return;
                case ClipboardDecision.Deny:
                    completion(false);
                    return;
            }

            if (pending.FirstOrDefault(item => item.View == view && item.Requester == requester && item.Access == access) is Pending waiting)
            {
                waiting.Waiters.Add(completion);
                return;
            }

            pending.Add(new Pending(Ids.New(), access, view, requester, new List<Action<bool>> { completion }));
            view.ClipboardPromptsChanged();
        }

        /// <summary>The prompt a view shows: its oldest.</summary>
        public object? FirstFor(IClipboardPromptView view)
        {
            return pending.FirstOrDefault(item => item.View == view) is Pending first
                ? new { id = first.Id, access = first.Access == ClipboardAccess.Read ? "read" : "write" }
                : null;
        }

        public void Answer(string id, bool allow, bool remember)
        {
            if (pending.FirstOrDefault(item => item.Id == id) is not Pending answered)
            {
                return;
            }

            if (remember)
            {
                policy.Remember(answered.Access, allow);
            }

            pending.Remove(answered);
            answered.View.ClipboardPromptsChanged();
            foreach (Action<bool> waiter in answered.Waiters)
            {
                waiter(allow);
            }
        }

        /// <summary>A view going away takes its prompts with it, refused.</summary>
        public void Drop(IClipboardPromptView view)
        {
            foreach (Pending gone in pending.Where(item => item.View == view).ToList())
            {
                pending.Remove(gone);
                gone.Waiters.ForEach(waiter => waiter(false));
            }
        }

        private sealed record Pending(string Id, ClipboardAccess Access, IClipboardPromptView View, PaneKey Requester, List<Action<bool>> Waiters);
    }

    /// <summary>
    /// The clipboard side of a view's terminals, as Ghostty's surface and agterm's callbacks do it: OSC 52 reads and writes,
    /// the Kitty clipboard protocol, and mode 5522 paste events. Reads ask the user unless allowed for the session; writes
    /// follow the clipboard-write setting.
    /// </summary>
    internal sealed class TerminalClipboardHost
    {
        private readonly Dictionary<PaneKey, KittyClipboard> kitty = new Dictionary<PaneKey, KittyClipboard>();
        private readonly IClipboardPromptView view;
        private readonly Action<PaneKey, string> reply;
        private readonly Func<string> readPolicy;
        private readonly Func<string> writePolicy;
        private readonly Func<PaneKey, bool> alive;
        private readonly Func<PaneKey, string?> readText;

        /// <param name="readPolicy">Ghostty's clipboard-read: ask, allow or deny.</param>
        /// <param name="writePolicy">Ghostty's clipboard-write: ask, allow or deny.</param>
        /// <param name="readText">The clipboard as the pane reads it: files as paths for its shell, as agterm's pasteboardText.</param>
        public TerminalClipboardHost(IClipboardPromptView view, Action<PaneKey, string> reply, Func<string> readPolicy, Func<string> writePolicy, Func<PaneKey, bool> alive, Func<PaneKey, string?> readText)
        {
            this.view = view;
            this.reply = reply;
            this.readPolicy = readPolicy;
            this.writePolicy = writePolicy;
            this.alive = alive;
            this.readText = readText;
        }

        /// <summary>Ghostty's policy: deny refuses, allow goes on at once, ask goes through the prompt.</summary>
        private void Decide(string policy, ClipboardAccess access, PaneKey pane, Action<bool> completion)
        {
            switch (policy)
            {
                case "deny":
                    completion(false);
                    break;
                case "allow":
                    completion(true);
                    break;
                default:
                    ClipboardPrompts.Shared.Request(access, view, pane, completion);
                    break;
            }
        }

        /// <summary>
        /// Ghostty's selection clipboard (OSC 52 "s"): the app's own, as agterm keeps it in a private pasteboard, so a program
        /// using it never replaces the system clipboard.
        /// </summary>
        internal static string? Selection { get; set; }

        public void Forget(PaneKey pane)
        {
            kitty.Remove(pane);
        }

        public void Osc52(PaneKey pane, string data)
        {
            if (Core.Osc52.Parse(data) is not Osc52Request request)
            {
                return;
            }

            if (request.Data is byte[] written)
            {
                // A denied write simply does not happen, as Ghostty's.
                Decide(writePolicy(), ClipboardAccess.Write, pane, allowed =>
                {
                    if (!allowed)
                    {
                        return;
                    }

                    if (request.Location == ClipboardLocation.Selection)
                    {
                        Selection = Encoding.UTF8.GetString(written);
                    }
                    else
                    {
                        SetClipboard(new[] { new ClipboardContent("text/plain", written) });
                    }
                });
                return;
            }

            // Ghostty does not even start a read under deny; otherwise a denied read gets empty contents, since OSC 52 has
            // no error answer and the program waits for one.
            string policy = readPolicy();
            if (policy == "deny")
            {
                return;
            }

            Decide(policy, ClipboardAccess.Read, pane, allowed =>
            {
                if (alive(pane))
                {
                    string text = !allowed ? string.Empty : request.Location == ClipboardLocation.Selection ? Selection ?? string.Empty : readText(pane) ?? string.Empty;
                    reply(pane, Core.Osc52.Reply(request.Location, text));
                }
            });
        }

        public void Osc5522(PaneKey pane, string data, bool bel)
        {
            if (!kitty.TryGetValue(pane, out KittyClipboard? state))
            {
                state = new KittyClipboard();
                kitty[pane] = state;
            }

            (string? answer, KittyClipboardRead? read, KittyClipboardWrite? write) = state.Handle(data, writeDenied: writePolicy() == "deny", bel ? KittyClipboard.Bel : KittyClipboard.St);
            if (answer is not null)
            {
                reply(pane, answer);
            }

            if (read is not null)
            {
                Read(pane, read);
            }

            if (write is not null)
            {
                Write(pane, write);
            }
        }

        /// <summary>
        /// Ghostty's paste in mode 5522: the program is told what the clipboard holds, with a password to read it; a paste
        /// from the selection clipboard (the middle button) says loc=primary.
        /// </summary>
        public void PasteEvent(PaneKey pane, bool selection)
        {
            if (!kitty.TryGetValue(pane, out KittyClipboard? state))
            {
                state = new KittyClipboard();
                kitty[pane] = state;
            }

            reply(pane, state.PasteEvent(Available(selection ? Selection : readText(pane)), selection));
        }

        private void Read(PaneKey pane, KittyClipboardRead read)
        {
            // Ghostty answers a read denied by policy with EPERM, so the program does not wait on it.
            string policy = readPolicy();
            if (policy == "deny")
            {
                reply(pane, KittyClipboard.Status("read", "EPERM", read.Id, read.Terminator));
                return;
            }

            if (!KittyClipboard.ReadNeedsClipboard(read))
            {
                reply(pane, KittyClipboard.ReadReply(read, null, Array.Empty<string>()));
                return;
            }

            if (policy == "allow" || read.Granted || KittyClipboard.ReadPromptExempt(read.Mimes.Count))
            {
                string? text = readText(pane);
                reply(pane, KittyClipboard.ReadReply(read, text, read.List ? Available(text) : Array.Empty<string>()));
                return;
            }

            Decide(policy, ClipboardAccess.Read, pane, allowed =>
            {
                if (!alive(pane))
                {
                    return;
                }

                string? text = allowed ? readText(pane) : null;
                reply(pane, allowed ? KittyClipboard.ReadReply(read, text, read.List ? Available(text) : Array.Empty<string>()) : KittyClipboard.Status("read", "EPERM", read.Id, read.Terminator));
            });
        }

        private void Write(PaneKey pane, KittyClipboardWrite write)
        {
            // The policy may have changed while the write was coming in; a session grant skips the prompt.
            string policy = writePolicy();
            Decide(write.Granted && policy == "ask" ? "allow" : policy, ClipboardAccess.Write, pane, allowed =>
            {
                if (!alive(pane))
                {
                    return;
                }

                if (!allowed)
                {
                    reply(pane, KittyClipboard.Status("write", "EPERM", write.Id, write.Terminator));
                    return;
                }

                // An empty commit clears the clipboard.
                IReadOnlyList<ClipboardContent> contents = write.Contents.Count > 0 ? write.Contents : new[] { new ClipboardContent("text/plain", Array.Empty<byte>()) };
                reply(pane, KittyClipboard.Status("write", SetClipboard(contents) ? "DONE" : "EIO", write.Id, write.Terminator));
            });
        }

        /// <summary>agterm lists only text, when the clipboard has it.</summary>
        private static IReadOnlyList<string> Available(string? text)
        {
            return text is null ? Array.Empty<string>() : new[] { "text/plain" };
        }

        /// <summary>
        /// Every representation goes on the clipboard, as agterm keeps them all: text as text, any other type under its MIME
        /// name as a clipboard format, and PNG also under the "PNG" name Windows programs read images by.
        /// </summary>
        private static bool SetClipboard(IReadOnlyList<ClipboardContent> contents)
        {
            DataObject data = new DataObject();
            foreach (ClipboardContent content in contents)
            {
                if (KittyClipboard.IsTextMime(content.Mime))
                {
                    if (!data.GetDataPresent(DataFormats.UnicodeText))
                    {
                        data.SetText(Encoding.UTF8.GetString(content.Data), TextDataFormat.UnicodeText);
                    }

                    continue;
                }

                data.SetData(content.Mime, new MemoryStream(content.Data));
                if (content.Mime == "image/png")
                {
                    data.SetData("PNG", new MemoryStream(content.Data));
                }
            }

            // Another program can hold the clipboard for a moment; WPF's own retry gives up on the first refusal.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return false;
                }
            }
        }
    }
}
