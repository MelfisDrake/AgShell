namespace AgShell.Core
{
    /// <summary>
    /// agterm's OpenPathResolver: a path the app is opened with ("open a terminal here") becomes the directory a new session
    /// starts in, the path itself when a directory, its folder when an existing file; null when it does not exist.
    /// </summary>
    public static class OpenPathResolver
    {
        public static string? Directory(string path, string currentDirectory)
        {
            // Explorer quotes a drive root as "C:\", which the command line reads as C:" with the backslash spent.
            string trimmed = path.Trim().Trim('"').Trim();
            if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
            {
                trimmed += "\\";
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed, currentDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (System.IO.Directory.Exists(full))
            {
                return full;
            }

            return File.Exists(full) ? Path.GetDirectoryName(full) : null;
        }
    }
}
