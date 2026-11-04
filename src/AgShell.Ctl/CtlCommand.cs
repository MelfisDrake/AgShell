using System.CommandLine;
using System.CommandLine.Invocation;

namespace AgShell.Ctl
{
    /// <summary>
    /// A command that keeps its options and arguments in the order they were added, as ArgumentParser keeps a command's
    /// properties, so its usage line reads as agterm's. System.CommandLine keeps the two in separate lists.
    /// </summary>
    internal sealed class CtlCommand : Command
    {
        public CtlCommand(string name, string? description = null)
            : base(name, description)
        {
        }

        /// <summary>agterm's defaultSubcommand, which this command stands in for when no subcommand is named.</summary>
        public Command? DefaultSubcommand { get; private set; }

        /// <summary>Adds the subcommand <paramref name="parent"/> runs when none is named, as agterm's defaultSubcommand.</summary>
        public static void AddDefault(Command parent, Command subcommand)
        {
            parent.Subcommands.Add(subcommand);
            ((CtlCommand)parent).DefaultSubcommand = subcommand;
        }

        /// <summary>agterm's discussion: examples and notes the help prints after the command's description.</summary>
        public string? Discussion { get; init; }

        /// <summary>The options and arguments in declaration order, and where the connection options stand.</summary>
        public List<object> Order { get; } = new List<object>();

        public new void Add(Option option)
        {
            Options.Add(option);
            Order.Add(option);
        }

        public new void Add(Argument argument)
        {
            Arguments.Add(argument);
            Order.Add(argument);
        }

        public new void Add(Command command)
        {
            Subcommands.Add(command);
        }

        /// <summary>agterm's @OptionGroup of BasicOptions or ClientOptions: --pipe, --json and, where the command has it, --window.</summary>
        public void Add(ConnectionOptions group)
        {
            Order.Add(group);
        }
    }

    /// <summary>
    /// The help, with a command's discussion after its description, where ArgumentParser prints it under the overview.
    /// </summary>
    internal sealed class DiscussionHelpAction : SynchronousCommandLineAction
    {
        private readonly SynchronousCommandLineAction help;

        public DiscussionHelpAction(SynchronousCommandLineAction help)
        {
            this.help = help;
        }

        public override int Invoke(ParseResult parseResult)
        {
            if (parseResult.CommandResult.Command is not CtlCommand { Discussion: string discussion })
            {
                return help.Invoke(parseResult);
            }

            TextWriter output = parseResult.InvocationConfiguration.Output;
            using StringWriter text = new StringWriter();
            parseResult.InvocationConfiguration.Output = text;
            int code;
            try
            {
                code = help.Invoke(parseResult);
            }
            finally
            {
                parseResult.InvocationConfiguration.Output = output;
            }

            string shown = text.ToString();
            string block = string.Join(Environment.NewLine, discussion.Split('\n').Select(line => line.TrimEnd('\r')).Select(line => line.Length == 0 ? "" : "  " + line)) + Environment.NewLine + Environment.NewLine;
            int usage = shown.IndexOf("Usage:", StringComparison.Ordinal);
            output.Write(usage < 0 ? shown + Environment.NewLine + block : shown[..usage] + block + shown[usage..]);
            return code;
        }
    }

    /// <summary>The place of the connection options in a command's usage line.</summary>
    internal sealed class ConnectionOptions
    {
        public static readonly ConnectionOptions Here = new ConnectionOptions();

        private ConnectionOptions()
        {
        }
    }
}
