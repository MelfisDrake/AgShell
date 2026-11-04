namespace AgShell.Protocol
{
    public sealed class CommandStderrFile : IDisposable
    {
        private CommandStderrFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static CommandStderrFile? TryCreate()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agshell-command-" + Guid.NewGuid().ToString("N") + ".err");
            try
            {
                using FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return new CommandStderrFile(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public byte[] Consume(int limit)
        {
            try
            {
                using FileStream reader = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long size = reader.Length;
                int wanted = (int)Math.Min(Math.Max(0, limit), size);
                reader.Position = size - wanted;
                byte[] bytes = new byte[wanted];
                int read = reader.ReadAtLeast(bytes, wanted, throwOnEndOfStream: false);
                return bytes[..read];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<byte>();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notice("CustomCommandRunner", "could not remove stderr capture: " + ex.Message);
            }
        }
    }
}
