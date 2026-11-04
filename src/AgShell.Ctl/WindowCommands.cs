using System.CommandLine;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>agterm's window commands: each window is a bundle of workspaces that can be closed and opened again.</summary>
    internal static partial class Commands
    {
        private static Command Window()
        {
            Command window = new CtlCommand("window", "Create, list, raise, arrange and close windows.");

            Argument<string?> newName = new Argument<string?>("name") { Description = "The window's name (default: window N).", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> minimized = new Option<bool>("--minimized") { Description = "Park it minimized right away, keeping the front window in front." };
            Command create = new CtlCommand("new", "Open a new window with one workspace and session; prints its id.") { newName, minimized };
            create.SetAction(result => Send(result, "window.new", null, new ControlArgs { Name = result.GetValue(newName), Minimized = Flag(result, minimized) }, echoId: true));
            window.Subcommands.Add(create);

            Command list = new CtlCommand("list", "List the windows, open and closed; --json adds their frames.");
            list.SetAction(result =>
            {
                if (Request(result, new ControlRequest { Cmd = "window.list" }) is not ControlReply reply)
                {
                    return 1;
                }

                if (result.GetValue(Json) || !reply.Response.Ok)
                {
                    return result.GetValue(Json) ? Print(reply.Line, reply.Response.Ok) : Error(reply.Response.Error ?? "unknown error");
                }

                Console.WriteLine(FormatWindows(reply.Response.Result?.Windows ?? new List<ControlWindowNode>()));
                return 0;
            });
            window.Subcommands.Add(list);

            window.Subcommands.Add(OnWindow("select", "Raise the window, or open it when it is closed.", "window.select"));
            window.Subcommands.Add(OnWindow("close", "Close the window; window select opens it again.", "window.close"));
            window.Subcommands.Add(OnWindow("delete", "Delete the window and its sessions for good; the last window stays.", "window.delete"));
            window.Subcommands.Add(OnWindow("zoom", "Toggle between the normal frame and a maximized one.", "window.zoom"));
            window.Subcommands.Add(OnWindow("fullscreen", "Toggle full screen.", "window.fullscreen"));

            Argument<string> renameTarget = new Argument<string>("id") { Description = "Window id, unique prefix, or 'active'." };
            Argument<string> renameName = new Argument<string>("name") { Description = "The new name." };
            Command rename = new CtlCommand("rename", "Rename the window.") { renameTarget, renameName };
            rename.SetAction(result => Send(result, "window.rename", result.GetValue(renameTarget), new ControlArgs { Name = result.GetValue(renameName) }));
            window.Subcommands.Add(rename);

            Argument<string?> minimizeTarget = WindowArgument();
            Argument<string?> minimizeMode = new Argument<string?>("mode") { Description = "on, off, or toggle (default).", Arity = ArgumentArity.ZeroOrOne };
            Command minimize = new CtlCommand("minimize", "Minimize the window, or bring it back.") { minimizeTarget, minimizeMode };
            minimize.SetAction(result =>
            {
                // Both are optional, so "window minimize on" means the active window.
                string? target = result.GetValue(minimizeTarget);
                string? mode = result.GetValue(minimizeMode);
                if (mode is null && target is "on" or "off" or "toggle")
                {
                    (target, mode) = (null, target);
                }

                return Send(result, "window.minimize", target ?? ControlTargets.Active, new ControlArgs { Mode = mode ?? "toggle" });
            });
            window.Subcommands.Add(minimize);

            Argument<string?> resizeTarget = WindowArgument();
            Option<int> width = new Option<int>("--width") { Description = "Width in the window's units.", Required = true };
            Option<int> height = new Option<int>("--height") { Description = "Height in the window's units.", Required = true };
            Command resize = new CtlCommand("resize", "Resize the window; prints the size applied.") { resizeTarget, width, height };
            resize.SetAction(result => Send(result, "window.resize", result.GetValue(resizeTarget) ?? ControlTargets.Active, new ControlArgs { Width = result.GetValue(width), Height = result.GetValue(height) }));
            window.Subcommands.Add(resize);

            Argument<string?> moveTarget = WindowArgument();
            Option<int> x = new Option<int>("--x") { Description = "Left edge from the display's left, without the taskbar.", Required = true };
            Option<int> y = new Option<int>("--y") { Description = "Top edge from the display's top.", Required = true };
            Option<int?> display = new Option<int?>("--display") { Description = "Display number, 0 being the primary (default: the window's own)." };
            Command move = new CtlCommand("move", "Move the window's top left corner.") { moveTarget, x, y, display };
            move.SetAction(result => Send(result, "window.move", result.GetValue(moveTarget) ?? ControlTargets.Active, new ControlArgs { X = result.GetValue(x), Y = result.GetValue(y), Display = result.GetValue(display) }));
            window.Subcommands.Add(move);

            Option<string> to = new Option<string>("--to") { Description = "next or prev.", Required = true };
            Command go = new CtlCommand("go", "Raise the next or previous open window.") { to };
            go.SetAction(result => Send(result, "window.go", null, new ControlArgs { To = result.GetValue(to) }));
            window.Subcommands.Add(go);

            return window;
        }

        private static Argument<string?> WindowArgument()
        {
            return new Argument<string?>("id") { Description = "Window id, unique prefix, or 'active' (default).", Arity = ArgumentArity.ZeroOrOne };
        }

        private static Command OnWindow(string name, string description, string cmd)
        {
            Argument<string?> target = WindowArgument();
            Command command = new CtlCommand(name, description) { target };
            command.SetAction(result => Send(result, cmd, result.GetValue(target) ?? ControlTargets.Active, null));
            return command;
        }

        private static int Print(string line, bool ok)
        {
            Console.WriteLine(line);
            return ok ? 0 : 1;
        }

        /// <summary>agterm's formatWindows: a window a line, "id  name", tagged [open] and [active]; frames only in --json.</summary>
        private static string FormatWindows(List<ControlWindowNode> windows)
        {
            return string.Join("\n", windows.Select(window => window.Id + "  " + window.Name + (window.Open ? " [open]" : string.Empty) + (window.Active ? " [active]" : string.Empty)));
        }
    }
}
