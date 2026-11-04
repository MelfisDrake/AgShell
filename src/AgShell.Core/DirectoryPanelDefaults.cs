namespace AgShell.Core
{
    /// <summary>
    /// agterm's DirectoryPanelDefaults: where a folder picker opens, the first of the given folders that exists, else the
    /// home folder, so a saved path that is gone falls through to the next.
    /// </summary>
    public static class DirectoryPanelDefaults
    {
        public static string Folder(params string?[] paths)
        {
            foreach (string? path in paths)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return path;
                }
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
