namespace AgShell.Core
{
    public sealed class ConfigEditorReload
    {
        private readonly Guid sessionId;
        private readonly string overlayId;
        private readonly string path;
        private readonly string? snapshot;
        private bool closed;

        public ConfigEditorReload(Guid sessionId, string overlayId, string path)
        {
            this.sessionId = sessionId;
            this.overlayId = overlayId;
            this.path = path;
            snapshot = Read(path);
        }

        public bool Close(Guid session, string overlay)
        {
            if (closed || session != sessionId || overlay != overlayId)
            {
                return false;
            }

            closed = true;
            return snapshot != Read(path);
        }

        private static string? Read(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
