using System.Runtime.InteropServices;
using System.Text;

namespace AgShell.Live
{
    /// <summary>
    /// The pane's terminal state, as zmx's daemon keeps a ghostty-vt terminal fed with the pty's output: a client coming
    /// back gets it serialized instead of the output again (agshell-vt.dll, native\agshell-vt).
    /// </summary>
    internal sealed partial class VtTerminal : IDisposable
    {
        private const string Library = "agshell-vt";

        private IntPtr handle;

        public VtTerminal(int columns, int rows)
        {
            handle = New(Clamp(columns), Clamp(rows), 0);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("agshell-vt could not create a terminal");
            }
        }

        public void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteBytes(handle, bytes, (nuint)bytes.Length);
        }

        /// <summary>zmx's resizeTerm: the size the snapshot is laid out for, without clearing the prompt for a redraw.</summary>
        public void Resize(int columns, int rows)
        {
            ResizeTerminal(handle, Clamp(columns), Clamp(rows));
        }

        /// <summary>zmx's serializeTerminalState with OSC 133;A rewritten to redraw=0; null when there is nothing to replay.</summary>
        public unsafe string? Snapshot()
        {
            IntPtr bytes = SnapshotBytes(handle, out nuint length);
            if (bytes == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString((byte*)bytes, checked((int)length));
            }
            finally
            {
                BytesFree(bytes, length);
            }
        }

        /// <summary>
        /// agterm's zmx screen: the plain text of the visible screen, or with <paramref name="all"/> of the history and the
        /// screen, soft-wrapped rows joined and trailing blanks trimmed, with the geometry and cursor; null when the read failed.
        /// </summary>
        public unsafe VtScreen? Screen(bool all)
        {
            if (!ScreenBytes(handle, all, out IntPtr bytes, out nuint length, out ScreenHeader header))
            {
                return null;
            }

            try
            {
                string text = length == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)bytes, checked((int)length));
                return new VtScreen(text, header.Columns, header.Rows, header.CursorX, header.CursorY, header.Alternate != 0);
            }
            finally
            {
                BytesFree(bytes, length);
            }
        }

        /// <summary>
        /// agterm's zmx encodeTyped: <paramref name="typed"/> as the keystrokes the program's keyboard mode asks for, each CR
        /// a Return key press and release; null when encoding failed.
        /// </summary>
        public unsafe string? EncodeTyped(string typed)
        {
            byte[] input = Encoding.UTF8.GetBytes(typed);
            IntPtr bytes = EncodeTypedBytes(handle, input, (nuint)input.Length, out nuint length);
            if (bytes == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return length == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)bytes, checked((int)length));
            }
            finally
            {
                BytesFree(bytes, length);
            }
        }

        /// <summary>zmx's isUserInput: a person typing (text, Return, a key), not the terminal's replies, mouse or focus reports.</summary>
        public static bool IsUserInput(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            return IsUserInputBytes(bytes, (nuint)bytes.Length);
        }

        /// <summary>zmx's term.getPwd: the working directory the program last reported with OSC 7, as reported; null when none.</summary>
        public unsafe string? Pwd()
        {
            IntPtr bytes = PwdBytes(handle, out nuint length);
            return bytes == IntPtr.Zero ? null : Encoding.UTF8.GetString((byte*)bytes, checked((int)length));
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                Free(handle);
                handle = IntPtr.Zero;
            }
        }

        private static ushort Clamp(int value) => (ushort)Math.Clamp(value, 1, ushort.MaxValue);

        [LibraryImport(Library, EntryPoint = "agshell_vt_new")]
        private static partial IntPtr New(ushort columns, ushort rows, nuint maxScrollbackLines);

        [LibraryImport(Library, EntryPoint = "agshell_vt_free")]
        private static partial void Free(IntPtr terminal);

        [LibraryImport(Library, EntryPoint = "agshell_vt_write")]
        private static partial void WriteBytes(IntPtr terminal, byte[] data, nuint length);

        [LibraryImport(Library, EntryPoint = "agshell_vt_resize")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool ResizeTerminal(IntPtr terminal, ushort columns, ushort rows);

        [LibraryImport(Library, EntryPoint = "agshell_vt_snapshot")]
        private static partial IntPtr SnapshotBytes(IntPtr terminal, out nuint length);

        [LibraryImport(Library, EntryPoint = "agshell_vt_screen")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool ScreenBytes(IntPtr terminal, [MarshalAs(UnmanagedType.U1)] bool all, out IntPtr bytes, out nuint length, out ScreenHeader header);

        [LibraryImport(Library, EntryPoint = "agshell_vt_encode_typed")]
        private static partial IntPtr EncodeTypedBytes(IntPtr terminal, byte[] typed, nuint length, out nuint outLength);

        [LibraryImport(Library, EntryPoint = "agshell_vt_is_user_input")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool IsUserInputBytes(byte[] data, nuint length);

        [LibraryImport(Library, EntryPoint = "agshell_vt_pwd")]
        private static partial IntPtr PwdBytes(IntPtr terminal, out nuint length);

        [LibraryImport(Library, EntryPoint = "agshell_vt_bytes_free")]
        private static partial void BytesFree(IntPtr bytes, nuint length);

        /// <summary>agshell-vt's ScreenHeader: agterm's zmx one without the revision, which the daemon keeps.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenHeader
        {
            public ushort Columns;
            public ushort Rows;
            public ushort CursorX;
            public ushort CursorY;
            public byte Alternate;
        }
    }

    /// <summary>One read of the daemon's terminal: its text, size and zero-based cursor, and whether the alternate screen is up.</summary>
    internal sealed record VtScreen(string Text, int Columns, int Rows, int CursorX, int CursorY, bool Alternate);
}
