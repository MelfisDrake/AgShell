namespace AgShell.App
{
    internal static class EventLogRegistration
    {
        internal const string ServiceSid = "S-1-5-80-880578595-1860270145-482643319-2788375705-1540778122";

        internal static string ResourceAccessScript(string resources)
        {
            return "try { $resource = [IO.FileInfo]::new('" + resources.Replace("'", "''") + "'); "
                + "$acl = $resource.GetAccessControl(); "
                + "$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new("
                + "[System.Security.Principal.SecurityIdentifier]::new('" + ServiceSid + "'), "
                + "[System.Security.AccessControl.FileSystemRights]::Read, [System.Security.AccessControl.AccessControlType]::Allow)); "
                + "$resource.SetAccessControl($acl) } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }; ";
        }

        internal static string RegisterScript(string manifest, string resources)
        {
            return ResourceAccessScript(resources)
                + "wevtutil um '" + manifest.Replace("'", "''") + "' 2>$null; wevtutil im '" + manifest.Replace("'", "''") + "' '/rf:" + resources.Replace("'", "''") + "' '/mf:" + resources.Replace("'", "''") + "'; exit $LASTEXITCODE";
        }
    }
}
