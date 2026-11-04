using System.Windows;
using System.Windows.Interop;

namespace AgShell.App
{
    /// <summary>agterm's toggle_fullscreen: the window without its frame, over the whole monitor and the taskbar.</summary>
    public partial class MainWindow
    {
        private WindowState? stateBeforeFullscreen;
        private Rect boundsBeforeFullscreen;

        private void ToggleFullscreen()
        {
            if (stateBeforeFullscreen is WindowState previous)
            {
                stateBeforeFullscreen = null;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Left = boundsBeforeFullscreen.Left;
                Top = boundsBeforeFullscreen.Top;
                Width = boundsBeforeFullscreen.Width;
                Height = boundsBeforeFullscreen.Height;
                WindowState = previous;
                SetMenuBarFullscreen(false);
                PaintTitleBar();
                PostState();
                return;
            }

            boundsBeforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            Rect monitor = Displays.MonitorArea(handle);
            double scale = ChromeGetDpiForWindow(handle) / 96.0;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = monitor.Left / scale;
            Top = monitor.Top / scale;
            Width = monitor.Width / scale;
            Height = monitor.Height / scale;
            SetMenuBarFullscreen(true);
            // agterm makes the window opaque in full screen and leaves the saved opacity alone.
            PaintTitleBar();
            PostState();
        }
    }
}
