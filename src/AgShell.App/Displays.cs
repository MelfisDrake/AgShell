using System.Runtime.InteropServices;
using System.Windows;

namespace AgShell.App
{
    /// <summary>The displays as agterm numbers them for window move: the primary first, then left to right, top to bottom.</summary>
    internal static class Displays
    {
        /// <summary>One display's work area (without the taskbar) in pixels, and its handle.</summary>
        internal sealed record Display(IntPtr Handle, Rect WorkArea, bool Primary);

        public static List<Display> All()
        {
            List<Display> displays = new List<Display>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, hdc, rect, data) =>
            {
                MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    Native work = info.Work;
                    displays.Add(new Display(monitor, new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), (info.Flags & 1) != 0));
                }

                return true;
            }, IntPtr.Zero);
            return displays.OrderByDescending(display => display.Primary).ThenBy(display => display.WorkArea.Left).ThenBy(display => display.WorkArea.Top).ToList();
        }

        internal static Rect MonitorArea(IntPtr window)
        {
            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info))
            {
                throw new InvalidOperationException("Could not locate the window's monitor.");
            }
            return new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        }

        /// <summary>The number of the display a window is mostly on.</summary>
        public static int Of(IntPtr window, List<Display> displays)
        {
            IntPtr monitor = MonitorFromWindow(window, 2);
            int index = displays.FindIndex(display => display.Handle == monitor);
            return Math.Max(0, index);
        }

        /// <summary>The number of the display the mouse pointer is on; null when it is on none of them.</summary>
        public static int? UnderPointer(List<Display> displays)
        {
            if (!GetCursorPos(out Point point))
            {
                return null;
            }

            IntPtr monitor = MonitorFromPoint(point, 0);
            int index = displays.FindIndex(display => display.Handle == monitor);
            return monitor == IntPtr.Zero || index < 0 ? null : index;
        }

        /// <summary>The display's scale, its effective DPI over 96.</summary>
        public static double Scale(Display display)
        {
            return GetDpiForMonitor(display.Handle, 0, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1;
        }

        /// <summary>The system's scale, its DPI over 96: what a window has before it is on a monitor.</summary>
        public static double SystemScale()
        {
            uint dpi = GetDpiForSystem();
            return dpi > 0 ? dpi / 96.0 : 1;
        }

        /// <summary>The frame a window shows on screen, in pixels, without the invisible resize borders; null when DWM cannot say.</summary>
        public static Rect? VisibleFrame(IntPtr window)
        {
            return DwmGetWindowAttribute(window, 9, out Native bounds, Marshal.SizeOf<Native>()) == 0
                ? new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                : null;
        }

        /// <summary>
        /// agterm's bestDisplayIndex: the display a frame overlaps most, or null when it overlaps none (a disconnected one).
        /// The frame and the work areas share one unit.
        /// </summary>
        public static int? BestFor(Rect frame, IReadOnlyList<Rect> areas)
        {
            int? best = null;
            double bestArea = 0;
            for (int index = 0; index < areas.Count; index++)
            {
                Rect overlap = Rect.Intersect(frame, areas[index]);
                double area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = index;
                }
            }

            return best;
        }

        /// <summary>
        /// agterm's WindowGeometry.constrain: a saved frame shrunk to the display, grown to the window's minimum, then held
        /// wholly on it, so a stale position cannot bring the window back with its title bar out of reach.
        /// </summary>
        public static Rect Constrain(Rect frame, Size min, Rect area)
        {
            double width = Math.Min(Math.Max(frame.Width, min.Width), area.Width);
            double height = Math.Min(Math.Max(frame.Height, min.Height), area.Height);
            double left = Math.Min(Math.Max(frame.Left, area.Left), area.Right - width);
            double top = Math.Min(Math.Max(frame.Top, area.Top), area.Bottom - height);
            return new Rect(left, top, width, height);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Native value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        private delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Native
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Native Monitor;
            public Native Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    }
}
