using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Protocol
{
    /// <summary>
    /// The check PipeOptions.CurrentUserOnly makes on a client, without its trap: .NET compares the pipe's owner with the
    /// caller's default owner, which for an administrator signed in over OpenSSH (a full, elevated token) is the
    /// Administrators group, so every pipe the user's own unelevated AgShell made is refused. A pipe the user owns is taken;
    /// one owned by Administrators only when the process serving it runs as this user, so another administrator's or a
    /// service's pipe is still refused.
    /// </summary>
    public static partial class PipeOwner
    {
        private const string Refusal = "the pipe is not owned by the current user";

        public static void Verify(NamedPipeClientStream pipe)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            SecurityIdentifier? owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            if (owner is not null && owner == current.User)
            {
                return;
            }

            if (owner is null || owner != current.Owner || ServerUser(pipe.SafePipeHandle) != current.User)
            {
                throw new UnauthorizedAccessException(Refusal);
            }
        }

        /// <summary>The user the process serving the pipe runs as; null when it cannot be told.</summary>
        private static SecurityIdentifier? ServerUser(SafePipeHandle pipe)
        {
            if (!OperatingSystem.IsWindows() || !GetNamedPipeServerProcessId(pipe, out uint processId))
            {
                return null;
            }

            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
                {
                    return null;
                }

                try
                {
                    using WindowsIdentity server = new WindowsIdentity(token);
                    return server.User;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        private const uint TokenQuery = 0x0008;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
