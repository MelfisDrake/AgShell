using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Protocol
{
    /// <summary>What <see cref="StateDirectoryLock.Acquire"/> found; only an owned lock grants the directory.</summary>
    public enum StateDirectoryClaim
    {
        Owned,
        HeldByAnother,
        Unavailable,
    }

    /// <summary>
    /// agterm's StateDirectoryLock: one process owns a state directory for as long as it lives, by an exclusive lock on a file
    /// in it that the system drops when the process dies. The file is never removed: the next process would lock a new file
    /// and exclude nobody.
    /// </summary>
    public sealed partial class StateDirectoryLock : IDisposable
    {
        public const string FileName = "agshell.lock";

        private readonly FileStream stream;

        private StateDirectoryLock(FileStream stream)
        {
            this.stream = stream;
        }

        public static string PathIn(string directory)
        {
            return Path.Combine(directory, FileName);
        }

        /// <summary>Creates a missing directory and takes the lock without waiting; <paramref name="detail"/> says why it is unavailable.</summary>
        public static StateDirectoryClaim Acquire(string directory, out StateDirectoryLock? owned, out string detail)
        {
            owned = null;
            detail = string.Empty;
            string path = PathIn(directory);
            FileStream stream;
            try
            {
                Directory.CreateDirectory(directory);
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                detail = "cannot open " + path + ": " + ex.Message;
                return StateDirectoryClaim.Unavailable;
            }

            if (Lock(stream.SafeFileHandle))
            {
                owned = new StateDirectoryLock(stream);
                return StateDirectoryClaim.Owned;
            }

            int error = Marshal.GetLastPInvokeError();
            stream.Dispose();
            if (error == ErrorLockViolation)
            {
                return StateDirectoryClaim.HeldByAnother;
            }

            detail = "cannot lock " + path + ": " + Marshal.GetPInvokeErrorMessage(error);
            return StateDirectoryClaim.Unavailable;
        }

        /// <summary>
        /// Whether a process holds the lock, null when that cannot be answered. As agterm's F_GETLK it observes the lock without
        /// taking it: a read of a locked range fails, so a starting instance never loses its own claim to the probe.
        /// </summary>
        public static bool? Held(string directory)
        {
            try
            {
                using FileStream stream = new FileStream(PathIn(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.ReadByte();
                return false;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorLockViolation)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            stream.Dispose();
        }

        private const int ErrorLockViolation = 33;
        private const uint LockfileFailImmediately = 0x1;
        private const uint LockfileExclusiveLock = 0x2;

        private static unsafe bool Lock(SafeFileHandle file)
        {
            NativeOverlapped overlapped = default;
            return LockFileEx(file, LockfileExclusiveLock | LockfileFailImmediately, 0, uint.MaxValue, uint.MaxValue, &overlapped);
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint bytesLow, uint bytesHigh, NativeOverlapped* overlapped);
    }
}
