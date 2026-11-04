using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using AgShell.Core;

namespace AgShell.App
{
    public partial class MainWindow
    {
        private readonly WindowChrome chrome = new WindowChrome
        {
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = true,
            CornerRadius = new CornerRadius(0),
        };
        private TitlebarComposition chromeIdentity = new TitlebarComposition("", null, "", "");

        private void InitChrome()
        {
            WindowChrome.SetWindowChrome(this, chrome);
            SourceInitialized += (_, _) =>
            {
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ChromeMessage);
                SyncChromeIdentity();
            };
            InitToolbar();
            SizeChanged += (_, _) => UpdateChromeGeometry();
            StateChanged += (_, _) => UpdateChromeGeometry();
        }

        private IntPtr ChromeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x02e0)
            {
                Dispatcher.BeginInvoke(UpdateChromeGeometry);
            }
            return IntPtr.Zero;
        }

        private void SyncChromeIdentity()
        {
            Session? session = model.SelectedSessionId is Guid id ? model.FindSession(id) : null;
            chromeIdentity = TitlebarComposition.Compose(session, session is null ? null : model.FindWorkspaceOf(session.Id), entry.CustomName, settings);
            if (model.Dashboard is not null && !model.Zoomed && settings.EffectiveToolbarMode != "hidden")
            {
                chromeIdentity = new TitlebarComposition(Strings.T("Дашборд") + (entry.CustomName is string name ? " — " + name : ""), null, "", "");
            }
            identityLayout.SetValue(TextElement.FontSizeProperty, settings.EffectiveInterfaceFontSize);
            identityTitle.Text = chromeIdentity.Title;
            identityContext.Text = chromeIdentity.Tail;
            identitySubtitle.Text = chromeIdentity.Subtitle;
            identitySubtitle.Visibility = chromeIdentity.Subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            PaintChrome();
            UpdateChromeGeometry();
        }

        private void PaintChrome()
        {
            if (theme is null) return;
            Brush background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Background));
            Brush foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Foreground));
            identityBackground.Background = new SolidColorBrush(((SolidColorBrush)background).Color) { Opacity = Translucent ? settings.EffectiveBackgroundOpacity : 1 };
            Color line = ((SolidColorBrush)foreground).Color;
            line.A = 64;
            identityBackground.BorderBrush = new SolidColorBrush(line);
            captionPaint.BorderBrush = identityBackground.BorderBrush;
            Thickness border = new Thickness(0, 0, 0, settings.EffectiveToolbarMode == "hidden" ? 0 : 1);
            identityBackground.BorderThickness = border;
            captionPaint.BorderThickness = border;
            captionPaint.Background = background;
            resizeEdgePaint.BorderBrush = background;
            foreach (TextBlock text in new[] { identityTitle, identityHost, identityContext, identitySubtitle })
            {
                text.Foreground = foreground;
            }
        }

        private void UpdateChromeGeometry()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            bool fullscreen = stateBeforeFullscreen is not null;
            bool visible = settings.EffectiveToolbarMode != "hidden";
            double height = visible ? Math.Max(settings.EffectiveToolbarMode == "normal" ? 48 : 30,
                SystemParameters.WindowCaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight) : 3;
            chromeRow.Height = new GridLength(height);
            identityLayout.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            bool nativeTop = !visible && !fullscreen && WindowState == WindowState.Normal;
            chrome.NonClientFrameEdges = nativeTop ? NonClientFrameEdges.Top : NonClientFrameEdges.None;
            double topResize = nativeTop ? SystemParameters.WindowResizeBorderThickness.Top : !visible || fullscreen ? 0 : 6;
            chrome.ResizeBorderThickness = fullscreen ? new Thickness(0) : new Thickness(6, topResize, 6, 6);
            chrome.GlassFrameThickness = new Thickness(0, visible && !fullscreen ? height : 0, 0, 0);
            uint dpi = ChromeGetDpiForWindow(handle);
            double scale = dpi / 96.0;
            int result = ChromeDwmGetWindowAttribute(handle, 5, out ChromeRect bounds, Marshal.SizeOf<ChromeRect>());
            double reserve = visible && !fullscreen && result == 0 ? Math.Max(0, bounds.Right - bounds.Left) / scale + 8 : 0;
            captionReserve.Width = new GridLength(reserve);
            if (WindowState == WindowState.Maximized && !fullscreen)
            {
                double horizontal = (ChromeGetSystemMetricsForDpi(32, dpi) + ChromeGetSystemMetricsForDpi(92, dpi)) / scale;
                double vertical = (ChromeGetSystemMetricsForDpi(33, dpi) + ChromeGetSystemMetricsForDpi(92, dpi)) / scale;
                chromeFrame.Margin = new Thickness(horizontal, vertical, horizontal, vertical);
            }
            else chromeFrame.Margin = new Thickness(0);
            ChromeGetWindowRect(handle, out ChromeRect rect);
            ChromePoint origin = new ChromePoint();
            ChromeClientToScreen(handle, ref origin);
            double offset = (origin.Y - rect.Top) / scale;
            captionPaint.Margin = new Thickness(0, Math.Clamp(bounds.Bottom / scale - offset - chromeFrame.Margin.Top, 0, height), 0, 0);
            chrome.CaptionHeight = fullscreen ? 0 : Math.Max(0, height + chromeFrame.Margin.Top + offset - topResize);
            // A child HWND takes hit tests before WindowChrome; leave the normal window's resize edges in WPF.
            Thickness contentInset = !fullscreen && WindowState == WindowState.Normal ? new Thickness(6, 0, 6, 6) : new Thickness(0);
            resizeEdgePaint.BorderThickness = contentInset;
            webView.Margin = contentInset;
            pageLayer.Margin = contentInset;
            double available = Math.Max(0, ActualWidth - chromeFrame.Margin.Left - chromeFrame.Margin.Right - reserve - 24);
            chromeActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            available = Math.Max(0, available - chromeActions.DesiredSize.Width);
            double leading = sidebar.Visible && !model.Zoomed && model.Dashboard is null && available - sidebar.Width - contentInset.Left >= 100 ? sidebar.Width + contentInset.Left : 0;
            double sidebarButtonWidth = chromeSidebarHost.Children.Count > 0 ? 28 : 0;
            identityLeading.Width = new GridLength(Math.Max(leading, sidebarButtonWidth));
            chromeSidebarHost.HorizontalAlignment = leading > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            available -= Math.Max(leading, sidebarButtonWidth);
            double hostWidth = Math.Min(240, available * 0.4);
            identityHost.MaxWidth = Math.Max(0, hostWidth - 18);
            identityHostGroup.Visibility = chromeIdentity.Host is null ? Visibility.Collapsed : Visibility.Visible;
            identityHost.Text = MiddleHost(chromeIdentity.Host, Math.Max(0, hostWidth - 18));
            identityHostGroup.Margin = new Thickness(chromeIdentity.Host is null || chromeIdentity.Title.Length == 0 ? 0 : 6, 0, 0, 0);
            identityHostGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            identityTitle.MaxWidth = Math.Max(0, available - identityHostGroup.DesiredSize.Width);

        }

        private string MiddleHost(string? host, double width)
        {
            if (host is null) return "";
            string text = host;
            int[] characters = StringInfo.ParseCombiningCharacters(host);
            for (int count = characters.Length; count > 1; count--)
            {
                FormattedText measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(identityHost.FontFamily, identityHost.FontStyle, identityHost.FontWeight, identityHost.FontStretch),
                    identityHost.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                if (measured.Width <= width) return text;
                int left = count / 2;
                int right = (count - 1) / 2;
                text = host[..characters[left]] + "…" + (right == 0 ? "" : host[characters[characters.Length - right]..]);
            }
            return "…";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChromeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ChromePoint { public int X, Y; }
        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint ChromeGetDpiForWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")]
        private static extern int ChromeGetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool ChromeGetWindowRect(IntPtr window, out ChromeRect rect);
        [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
        private static extern bool ChromeClientToScreen(IntPtr window, ref ChromePoint point);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int ChromeDwmGetWindowAttribute(IntPtr window, int attribute, out ChromeRect value, int size);
    }
}
