using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgShell.App
{
    /// <summary>
    /// agterm draws the HUD above the whole deck, a pane's page included, and the page under it stays live. A page here is a
    /// native window over the interface's browser, so the HUD the page laid out is drawn again in a transparent window of its
    /// own over the part of it a raised page covers; the window takes no focus and lets clicks through, except a Ctrl+click
    /// on a link, as agterm's HudLinkClick.
    /// </summary>
    internal sealed class HudFloat
    {
        private const int ExStyle = -20;
        private const int Transparent = 0x20;
        private const int ToolWindow = 0x80;
        private const int NoActivate = 0x08000000;
        private const int ControlKey = 0x11;

        private readonly Window owner;
        private readonly FrameworkElement layer;
        private readonly Func<IEnumerable<Rect>> raisedPages;
        private readonly Action<string> openLink;
        private readonly DispatcherTimer spinnerTimer = new DispatcherTimer();
        private readonly DispatcherTimer linkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly List<(TextBlock Line, Hyperlink Link)> links = new List<(TextBlock, Hyperlink)>();
        private Window? window;
        private JsonElement? shown;
        private Run? glyphRun;
        private string[] frames = Array.Empty<string>();
        private int frame;
        private bool clickable;

        public HudFloat(Window owner, FrameworkElement layer, Func<IEnumerable<Rect>> raisedPages, Action<string> openLink)
        {
            this.owner = owner;
            this.layer = layer;
            this.raisedPages = raisedPages;
            this.openLink = openLink;
            spinnerTimer.Tick += (sender, args) =>
            {
                frame++;
                if (glyphRun is not null && frames.Length > 0)
                {
                    glyphRun.Text = frames[frame % frames.Length] + " ";
                }
            };
            linkTimer.Tick += (sender, args) => UpdateClickThrough();
        }

        /// <summary>The selected session's HUD as its page laid it out, or null when there is none.</summary>
        public void Set(JsonElement? hud)
        {
            shown = hud;
            Refresh();
        }

        /// <summary>Shows the window over the HUD while a raised page crosses it, and hides it otherwise.</summary>
        public void Refresh()
        {
            if (shown is not JsonElement hud || !hud.TryGetProperty("width", out JsonElement widthValue) || widthValue.GetDouble() <= 0)
            {
                Hide();
                return;
            }

            Rect bounds = new Rect(hud.GetProperty("x").GetDouble(), hud.GetProperty("y").GetDouble(), widthValue.GetDouble(), hud.GetProperty("height").GetDouble());
            if (!raisedPages().Any(page => page.IntersectsWith(bounds)) || !owner.IsVisible || owner.WindowState == WindowState.Minimized)
            {
                Hide();
                return;
            }

            window ??= Create();
            Build(window, hud, bounds);
            Point screen = layer.PointToScreen(new Point(bounds.X, bounds.Y));
            Matrix fromDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point place = fromDevice.Transform(screen);
            window.Left = place.X;
            window.Top = place.Y;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
            if (!window.IsVisible)
            {
                window.Show();
            }

            linkTimer.IsEnabled = links.Count > 0;
        }

        public void Hide()
        {
            spinnerTimer.Stop();
            linkTimer.Stop();
            window?.Hide();
        }

        public void Close()
        {
            Hide();
            window?.Close();
            window = null;
        }

        private Window Create()
        {
            Window created = new Window
            {
                Owner = owner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                ResizeMode = ResizeMode.NoResize,
            };
            created.SourceInitialized += (sender, args) =>
            {
                IntPtr handle = new WindowInteropHelper(created).Handle;
                SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | Transparent | ToolWindow | NoActivate);
                clickable = false;
            };
            return created;
        }

        /// <summary>The same panel the page drew: its background and border, then each row at the place the page put it.</summary>
        private void Build(Window target, JsonElement hud, Rect bounds)
        {
            links.Clear();
            glyphRun = null;
            Canvas grid = new Canvas { ClipToBounds = true };
            FontFamily family = new FontFamily(hud.GetProperty("fontFamily").GetString() ?? "Cascadia Mono");
            double fontSize = hud.GetProperty("fontSize").GetDouble();
            double lineHeight = hud.GetProperty("lineHeight").GetDouble();
            Brush foreground = Css(hud.GetProperty("color").GetString());
            int glyphRow = hud.GetProperty("glyphRow").GetInt32();
            frames = hud.GetProperty("frames").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            int index = 0;
            foreach (JsonElement row in hud.GetProperty("rows").EnumerateArray())
            {
                TextBlock line = new TextBlock
                {
                    FontFamily = family,
                    FontSize = fontSize,
                    LineHeight = lineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = foreground,
                };
                if (index == glyphRow && frames.Length > 0)
                {
                    glyphRun = new Run(frames[frame % frames.Length] + " ");
                    line.Inlines.Add(glyphRun);
                }

                foreach (JsonElement run in row.GetProperty("runs").EnumerateArray())
                {
                    line.Inlines.Add(Styled(run, line));
                }

                Canvas.SetLeft(line, row.GetProperty("x").GetDouble());
                Canvas.SetTop(line, row.GetProperty("y").GetDouble());
                grid.Children.Add(line);
                index++;
            }

            if (glyphRun is not null)
            {
                spinnerTimer.Interval = TimeSpan.FromMilliseconds(hud.GetProperty("interval").GetInt32());
                spinnerTimer.Start();
            }
            else
            {
                spinnerTimer.Stop();
            }

            bool framed = hud.GetProperty("framed").GetBoolean();
            target.Content = new Border
            {
                Background = Css(hud.GetProperty("background").GetString()),
                BorderBrush = framed ? new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)) : null,
                BorderThickness = new Thickness(framed ? 1 : 0),
                CornerRadius = new CornerRadius(framed ? 8 : 0),
                Padding = new Thickness(hud.GetProperty("paddingWidth").GetDouble(), hud.GetProperty("paddingHeight").GetDouble(), hud.GetProperty("paddingWidth").GetDouble(), hud.GetProperty("paddingHeight").GetDouble()),
                Child = grid,
                Width = bounds.Width,
                Height = bounds.Height,
            };
        }

        /// <summary>agterm's SGR for a run: bold, italic, strikethrough, faint at half strength, underline; a link opens on Ctrl+click.</summary>
        private Inline Styled(JsonElement run, TextBlock line)
        {
            int style = run.GetProperty("style").GetInt32();
            Run text = new Run(run.GetProperty("text").GetString() ?? string.Empty)
            {
                FontWeight = (style & 1) != 0 ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = (style & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
            };
            TextDecorationCollection decorations = new TextDecorationCollection();
            if ((style & 16) != 0)
            {
                decorations.Add(TextDecorations.Underline);
            }

            if ((style & 4) != 0)
            {
                decorations.Add(TextDecorations.Strikethrough);
            }

            text.TextDecorations = decorations;
            if ((style & 8) != 0 && line.Foreground is SolidColorBrush solid)
            {
                text.Foreground = new SolidColorBrush(Color.FromArgb((byte)(solid.Color.A / 2), solid.Color.R, solid.Color.G, solid.Color.B));
            }

            if (run.TryGetProperty("link", out JsonElement link) && link.GetString() is string target)
            {
                Hyperlink hyperlink = new Hyperlink(text) { Foreground = line.Foreground, TextDecorations = text.TextDecorations, Cursor = System.Windows.Input.Cursors.Hand };
                hyperlink.Click += (sender, args) =>
                {
                    if ((GetAsyncKeyState(ControlKey) & 0x8000) != 0)
                    {
                        openLink(target);
                    }
                };
                links.Add((line, hyperlink));
                return hyperlink;
            }

            return text;
        }

        /// <summary>With Ctrl held over a link the window takes the click; otherwise every click goes through to the page.</summary>
        private void UpdateClickThrough()
        {
            if (window is null || !window.IsVisible)
            {
                return;
            }

            bool over = (GetAsyncKeyState(ControlKey) & 0x8000) != 0 && GetCursorPos(out ScreenPoint cursor)
                && links.Any(entry => entry.Line.IsVisible && LinkContains(entry.Line, entry.Link, entry.Line.PointFromScreen(new Point(cursor.X, cursor.Y))));
            if (over == clickable)
            {
                return;
            }

            clickable = over;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            int styles = GetWindowLong(handle, ExStyle);
            SetWindowLong(handle, ExStyle, over ? styles & ~Transparent : styles | Transparent);
        }

        internal static bool LinkContains(TextBlock line, Hyperlink link, Point point)
        {
            // Hit-test the inline directly: a transparent window never acquires IsMouseOver.
            DependencyObject? hit = ((IContentHost)line).InputHitTest(point) as DependencyObject;
            while (hit is not null && hit != line)
            {
                if (hit == link)
                {
                    return true;
                }

                hit = LogicalTreeHelper.GetParent(hit);
            }

            return false;
        }

        /// <summary>A CSS color as the page computed it: rgb(), rgba() or #hex; transparent when it is none of them.</summary>
        private static Brush Css(string? css)
        {
            if (string.IsNullOrWhiteSpace(css))
            {
                return Brushes.Transparent;
            }

            if (css.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = css[(css.IndexOf('(') + 1)..css.LastIndexOf(')')].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                double alpha = parts.Length > 3 ? double.Parse(parts[3].TrimEnd('%'), CultureInfo.InvariantCulture) * (parts[3].EndsWith('%') ? 0.01 : 1) : 1;
                return new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), Channel(parts[0]), Channel(parts[1]), Channel(parts[2])));
            }

            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(css));
            }
            catch (FormatException)
            {
                return Brushes.Transparent;
            }
        }

        private static byte Channel(string value)
        {
            return (byte)Math.Clamp(Math.Round(double.Parse(value, CultureInfo.InvariantCulture)), 0, 255);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out ScreenPoint point);
    }
}
