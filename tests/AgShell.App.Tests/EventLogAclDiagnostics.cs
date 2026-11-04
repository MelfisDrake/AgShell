using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit.Abstractions;

namespace AgShell.App.Tests
{
    internal sealed class EventLogAclDiagnostics
    {
        private readonly ITestOutputHelper output;
        private readonly string fixture;

        internal EventLogAclDiagnostics(ITestOutputHelper output, string fixture)
        {
            this.output = output;
            this.fixture = Path.GetFullPath(fixture);
        }

        internal void Environment(string resource)
        {
            CheckPath(resource);
            string icacls = Path.Combine(System.Environment.SystemDirectory, "icacls.exe");
            output.WriteLine("ACL diagnostic v1; Windows=" + RuntimeInformation.OSDescription
                + "; OSVersion=" + System.Environment.OSVersion.Version
                + "; runtime=" + RuntimeInformation.FrameworkDescription
                + "; runtimeVersion=" + System.Environment.Version
                + "; process=" + RuntimeInformation.ProcessArchitecture
                + "; AccessControl=" + typeof(FileSecurity).Assembly.GetName().Version
                + "; icacls=" + FileVersionInfo.GetVersionInfo(icacls).FileVersion);
            output.WriteLine("identity exactSID=" + WindowsIdentity.GetCurrent().User?.Value
                + "; service exactSID=" + AgShell.App.EventLogRegistration.ServiceSid);
            string temp = Path.GetFullPath(Path.GetTempPath());
            output.WriteLine("pathshape: fixture directly under temp=" + (Path.GetDirectoryName(fixture) == temp.TrimEnd(Path.DirectorySeparatorChar))
                + "; drive=" + Path.GetPathRoot(fixture)
                + "; depth=" + fixture.Split(Path.DirectorySeparatorChar).Length
                + "; resource=" + Path.GetFileName(resource)
                + "; filesystem=" + new DriveInfo(Path.GetPathRoot(fixture)!).DriveFormat);
            output.WriteLine("Only owned fixture ACLs are read; inherited ACE source is recorded as inherited from fixture-parent chain, not resolved by reading external ancestors.");
        }

        internal byte[] Persisted(string stage, string path)
        {
            CheckPath(path);
            // Keep both native API views: legacy inheritance flags can differ from GetNamedSecurityInfo.
            const uint ownerGroupDacl = 0x7;
            bool first = GetFileSecurity(path, ownerGroupDacl, null, 0, out uint length);
            int error = Marshal.GetLastWin32Error();
            if (first || error != 122)
            {
                throw new Win32Exception(error, "GetFileSecurity size query: " + stage);
            }
            byte[] binary = new byte[length];
            if (!GetFileSecurity(path, ownerGroupDacl, binary, length, out uint actual))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileSecurity: " + stage);
            }
            Array.Resize(ref binary, checked((int)actual));
            string label = stage + " " + (path == fixture ? "<fixture>" : "<fixture>/" + Path.GetFileName(path));
            Descriptor(label + " GetFileSecurity", binary);
            uint result = GetNamedSecurityInfo(path, 1, ownerGroupDacl, out IntPtr owner, out IntPtr group,
                out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result), "GetNamedSecurityInfo: " + stage);
            }
            try
            {
                byte[] named = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, named, 0, named.Length);
                Descriptor(label + " GetNamedSecurityInfo", named);
                return named;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        internal void Managed(string stage, FileSecurity security)
        {
            Descriptor(stage + " managed", security.GetSecurityDescriptorBinaryForm());
            output.WriteLine("protected=" + security.AreAccessRulesProtected + "; canonical=" + security.AreAccessRulesCanonical);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                output.WriteLine("managedACE SID=" + rule.IdentityReference.Value
                    + "; type=" + rule.AccessControlType + "; mask=0x" + ((uint)rule.FileSystemRights).ToString("X8")
                    + "; inheritance=" + rule.InheritanceFlags + "; propagation=" + rule.PropagationFlags
                    + "; inherited=" + rule.IsInherited);
            }
        }

        private void Descriptor(string stage, byte[] binary)
        {
            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(binary, 0);
            output.WriteLine("--- " + stage);
            output.WriteLine("binary=" + Convert.ToHexString(binary));
            output.WriteLine("SDDL=" + descriptor.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access));
            output.WriteLine("owner=" + descriptor.Owner?.Value + "; group=" + descriptor.Group?.Value
                + "; protected=" + descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
                + "; autoInherited=" + descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited)
                + "; autoInheritRequired=" + descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInheritRequired)
                + "; flags=" + descriptor.ControlFlags + "; flagsHex=0x" + ((int)descriptor.ControlFlags).ToString("X4"));
            if (descriptor.DiscretionaryAcl == null)
            {
                output.WriteLine("DACL=null");
                return;
            }
            int index = 0;
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            {
                byte[] aceBinary = new byte[ace.BinaryLength];
                ace.GetBinaryForm(aceBinary, 0);
                KnownAce? known = ace as KnownAce;
                output.WriteLine("rawACE[" + index++ + "] type=" + ace.AceType
                    + "; SID=" + known?.SecurityIdentifier.Value
                    + "; mask=" + (known == null ? "n/a" : "0x" + ((uint)known.AccessMask).ToString("X8"))
                    + "; flags=" + ace.AceFlags + "; inherited=" + ace.IsInherited
                    + "; source=" + (ace.IsInherited ? "parent-chain (see owned parent snapshot)" : "explicit")
                    + "; binary=" + Convert.ToHexString(aceBinary));
            }
        }

        private void CheckPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full != fixture && Path.GetDirectoryName(full) != fixture)
            {
                throw new InvalidOperationException("ACL diagnostics are restricted to the owned fixture and its direct children");
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "GetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileSecurity(string fileName, uint information, byte[]? descriptor, uint length, out uint needed);

        [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
        private static extern uint GetNamedSecurityInfo(string name, int type, uint information, out IntPtr owner,
            out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
