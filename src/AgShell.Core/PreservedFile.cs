namespace AgShell.Core
{
    public static class PreservedFile
    {
        public static string MoveAside(string path, string reason)
        {
            string destination = path + "." + reason + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            File.Move(path, destination);
            return destination;
        }
    }
}
