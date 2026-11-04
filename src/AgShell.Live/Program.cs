namespace AgShell.Live
{
    /// <summary>
    /// agshell-live.exe PIPE: the daemon of one live pane, as agterm's zmx daemon is of one. It serves the pane's pipe and
    /// leaves with its shell.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                return 2;
            }

            using Mutex instance = new Mutex(true, @"Local\" + args[0], out bool first);
            if (!first)
            {
                return 0;
            }

            new PaneDaemon(args[0]).RunAsync().GetAwaiter().GetResult();
            return 0;
        }
    }
}
