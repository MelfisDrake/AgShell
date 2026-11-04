using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AgShell.App
{
    public partial class MainWindow
    {
        private JsonElement[] toolbarItems = Array.Empty<JsonElement>();
        private readonly Dictionary<string, Button> toolbarButtons = new Dictionary<string, Button>();
        private Popup? toolbarPopup;
        private string? toolbarPopover;
        private string toolbarRows = "";
        private string toolbarLayout = "";
        private string toolbarPalette = "";
        private IntPtr toolbarReturnFocus;
        private Brush toolbarBackground = Brushes.Black;
        private Brush toolbarForeground = Brushes.White;
        private Brush toolbarHover = Brushes.Gray;
        private double toolbarFont = 13;
        private bool toolbarClosing;
        private bool toolbarHeld;

        private bool ToolbarAttentionEnabled => UiActionsEnabled && !toolbarHeld && ClipboardPrompts.Shared.FirstFor(this) is null;

        private void InitToolbar()
        {
            Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!ToolbarOwnsForeground()) CloseToolbarPopover(false);
            });
            LocationChanged += (_, _) => CloseToolbarPopover(true);
            SizeChanged += (_, _) => CloseToolbarPopover(true);
            Closed += (_, _) => { toolbarClosing = true; CloseToolbarPopover(false); };
        }

        private void ApplyToolbar(JsonElement message)
        {
            bool held = message.GetProperty("held").GetBoolean();
            if (toolbarHeld != held)
            {
                toolbarHeld = held;
                Library.RaiseWindowsChanged();
            }
            toolbarItems = message.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
            JsonElement colors = message.GetProperty("colors");
            toolbarBackground = MenuBrush(colors, "bar");
            toolbarForeground = MenuBrush(colors, "text");
            toolbarHover = MenuBrush(colors, "popoverHover");
            toolbarFont = message.GetProperty("fontSize").GetDouble();
            bool recolor = toolbarPalette != colors.GetRawText();
            toolbarPalette = colors.GetRawText();
            string layout = string.Join("|", toolbarItems.Select(item => ToolbarString(item, "id")));
            if (toolbarLayout != layout)
            {
                CloseToolbarPopover(true);
                toolbarLayout = layout;
                chromeActions.Children.Clear();
                chromeSidebarHost.Children.Clear();
                toolbarButtons.Clear();
                foreach (JsonElement item in toolbarItems)
                {
                    string id = ToolbarString(item, "id");
                    if (ToolbarFlag(item, "separator"))
                    {
                        chromeActions.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), Background = toolbarForeground, Opacity = 0.25 });
                        continue;
                    }
                    Button button = ToolbarButton();
                    button.Tag = id;
                    button.Width = 28;
                    button.Height = 26;
                    button.Padding = new Thickness(4);
                    button.PreviewMouseLeftButtonDown += (_, _) => toolbarReturnFocus = ToolbarGetFocus();
                    button.Click += (_, _) => ToolbarClick(id);
                    AutomationProperties.SetAutomationId(button, "toolbar-" + id);
                    WindowChrome.SetIsHitTestVisibleInChrome(button, true);
                    toolbarButtons[id] = button;
                    if (id == "sidebar") chromeSidebarHost.Children.Add(button);
                    else chromeActions.Children.Add(button);
                }
            }
            foreach (JsonElement item in toolbarItems)
            {
                string id = ToolbarString(item, "id");
                if (!toolbarButtons.TryGetValue(id, out Button? button)) continue;
                if (recolor) button.Template = ToolbarButton().Template;
                button.IsEnabled = ToolbarFlag(item, "enabled");
                button.ToolTip = ToolbarString(item, "title");
                AutomationProperties.SetName(button, ToolbarString(item, "title"));
                AutomationProperties.SetItemStatus(button, ToolbarString(item, "glyph"));
                button.Content = ToolbarGlyph(ToolbarString(item, "glyph"), ToolbarBrush(item, toolbarForeground), settings.EffectiveToolbarMode == "normal" ? 17 : 14);
                button.Foreground = toolbarForeground;
            }
            UpdateChromeGeometry();
            if (toolbarPopover is string open)
            {
                JsonElement? anchor = ToolbarFind(open);
                if (anchor is not JsonElement item || !ToolbarFlag(item, "enabled") || model.Pick is not null || model.GuiAsk is not null)
                    CloseToolbarPopover(true);
                else UpdateToolbarRows(item);
            }
        }

        private JsonElement? ToolbarFind(string id)
        {
            foreach (JsonElement item in toolbarItems)
            {
                if (ToolbarString(item, "id") == id) return item;
                if (item.TryGetProperty("rows", out JsonElement rows))
                    foreach (JsonElement row in rows.EnumerateArray())
                        if (ToolbarString(row, "id") == id) return row;
            }
            return null;
        }

        private void ToolbarClick(string id)
        {
            if (ToolbarFind(id) is not JsonElement item || !ToolbarFlag(item, "enabled")) return;
            if (model.Pick is not null || model.GuiAsk is not null || (!UiActionsEnabled && id is not "zoom" and not "dashboard-exit")) return;
            if (!item.TryGetProperty("rows", out _))
            {
                CloseToolbarPopover(true);
                webView.Focus();
                Post(new { type = "toolbarAction", id });
                return;
            }
            if (toolbarPopover == id) { CloseToolbarPopover(true); return; }
            CloseToolbarPopover(false);
            if (toolbarReturnFocus == IntPtr.Zero) toolbarReturnFocus = ToolbarGetFocus();
            toolbarPopover = id;
            toolbarPopoverToken++;
            toolbarRows = "";
            toolbarPopup = new Popup
            {
                PlacementTarget = toolbarButtons[id], Placement = PlacementMode.Custom,
                StaysOpen = false, AllowsTransparency = true,
                CustomPopupPlacementCallback = (size, target, offset) =>
                {
                    Point anchor = toolbarButtons[id].TranslatePoint(new Point(), this);
                    double x = Math.Clamp(target.Width / 2 - size.Width / 2, 4 - anchor.X, Math.Max(4 - anchor.X, ActualWidth - anchor.X - size.Width - 4));
                    return new[] { new CustomPopupPlacement(new Point(x, target.Height + 6), PopupPrimaryAxis.Horizontal) };
                },
            };
            toolbarPopup.Closed += (_, _) => CloseToolbarPopover(true);
            UpdateToolbarRows(item);
            Post(new { type = "toolbarPopover", value = true });
            toolbarPopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (toolbarPopup?.Child is not FrameworkElement child || !toolbarPopup.IsOpen || !ToolbarOwnsForeground()) return;
                // Popup's HWND must own native focus before WPF keyboard navigation can own real keystrokes.
                if (PresentationSource.FromVisual(child) is HwndSource source)
                {
                    source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (ForwardToolbarKey(message, wParam, lParam)) handled = true;
                        if (message is 0x0008 or 0x0006) Dispatcher.BeginInvoke(() => { if (!ToolbarOwnsForeground()) CloseToolbarPopover(false); });
                        return IntPtr.Zero;
                    });
                    ToolbarSetFocus(source.Handle);
                }
                if (!child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) child.Focus();
            });
        }

        private void UpdateToolbarRows(JsonElement anchor)
        {
            string json = anchor.GetRawText() + toolbarFont + toolbarBackground + toolbarForeground + toolbarHover;
            if (json == toolbarRows || toolbarPopup is null) return;
            toolbarRows = json;
            string? focused = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
            ScrollViewer? previous = toolbarPopup.Child is Grid previousFrame
                ? previousFrame.Children.OfType<Border>().Select(border => border.Child).OfType<ScrollViewer>().FirstOrDefault() : null;
            double scrollOffset = previous?.VerticalOffset ?? 0;
            bool ownsKeyboard = toolbarPopup.IsOpen && ToolbarOwnsForeground();
            StackPanel rows = new StackPanel();
            rows.SetValue(TextElement.FontSizeProperty, toolbarFont);
            bool commands = toolbarPopover == "commands";
            double unit = toolbarFont / 13;
            foreach (JsonElement row in anchor.GetProperty("rows").EnumerateArray())
            {
                if (ToolbarFlag(row, "separator"))
                {
                    rows.Children.Add(new Border { Height = 1, Background = toolbarForeground, Opacity = 0.25, Margin = new Thickness(8, 4, 8, 4) });
                    continue;
                }
                string id = ToolbarString(row, "id");
                Button button = ToolbarButton();
                button.Tag = id;
                button.IsEnabled = ToolbarFlag(row, "enabled");
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Padding = new Thickness(12 * unit, 6 * unit, 12 * unit, 6 * unit);
                button.Margin = new Thickness(0, 1, 0, 1);
                Grid content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition());
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel text = new StackPanel();
                DockPanel title = new DockPanel();
                if (ToolbarFlag(row, "remote")) title.Children.Add(ToolbarGlyph("cloud", toolbarForeground, 14));
                TextBlock name = new TextBlock { Text = ToolbarString(row, "title"), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = commands ? FontWeights.Normal : FontWeights.SemiBold };
                if (commands) ToolbarMiddleText(name, ToolbarString(row, "title"));
                title.Children.Add(name);
                text.Children.Add(title);
                if (row.TryGetProperty("subtitle", out _))
                {
                    TextBlock subtitle = new TextBlock { Opacity = 0.6, FontSize = toolbarFont * 0.923, TextTrimming = TextTrimming.CharacterEllipsis };
                    ToolbarMiddleText(subtitle, ToolbarString(row, "subtitle"));
                    text.Children.Add(subtitle);
                }
                content.Children.Add(text);
                if (commands)
                {
                    TextBlock shortcut = new TextBlock { Text = ToolbarString(row, "shortcut"), Margin = new Thickness(12, 0, 0, 0), Opacity = 0.6, FontSize = toolbarFont * 0.923, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(shortcut, 1);
                    content.Children.Add(shortcut);
                }
                else if (row.TryGetProperty("shape", out _))
                {
                    FrameworkElement glyph = ToolbarStatus(ToolbarString(row, "shape"), ToolbarBrush(row, toolbarForeground));
                    glyph.Margin = new Thickness(10, 0, 0, 0);
                    Grid.SetColumn(glyph, 1);
                    content.Children.Add(glyph);
                }
                button.Content = content;
                AutomationProperties.SetName(button, ToolbarString(row, "title"));
                AutomationProperties.SetAutomationId(button, "toolbar-row-" + id);
                button.Click += (_, _) =>
                {
                    if (ToolbarFind(id) is not JsonElement current || !ToolbarFlag(current, "enabled") || model.Pick is not null || model.GuiAsk is not null) return;
                    CloseToolbarPopover(true);
                    Dispatcher.BeginInvoke(() => Post(new { type = "toolbarAction", id }));
                };
                rows.Children.Add(button);
            }
            rows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = commands ? Math.Clamp(rows.DesiredSize.Width + 12, 180 * unit, 320 * unit) : 320 * unit;
            ScrollViewer scroll = new ScrollViewer { Focusable = false, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Min((commands ? 400 * unit - 14 : 440 * unit), Math.Max(100, ActualHeight - chromeRow.ActualHeight - 50)) };
            Border panel = new Border { Background = toolbarBackground, BorderBrush = new SolidColorBrush(((SolidColorBrush)toolbarForeground).Color) { Opacity = 0.25 }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Width = width, Child = scroll };
            panel.SetValue(TextElement.FontSizeProperty, toolbarFont);
            panel.SetValue(TextElement.ForegroundProperty, toolbarForeground);
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            Grid frame = new Grid { Margin = new Thickness(0, 8, 0, 0), Focusable = true };
            KeyboardNavigation.SetIsTabStop(frame, false);
            KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
            frame.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; CloseToolbarPopover(true); }
            };
            frame.Children.Add(panel);
            Path arrow = new Path { Data = Geometry.Parse("M0 8L8 0L16 8Z"), Fill = toolbarBackground, Stroke = new SolidColorBrush(((SolidColorBrush)toolbarForeground).Color) { Opacity = 0.25 }, StrokeThickness = 1, Width = 16, Height = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -7, 0, 0), IsHitTestVisible = false };
            frame.Children.Add(arrow);
            frame.Loaded += (_, _) =>
            {
                if (toolbarPopover is not string id || !toolbarButtons.TryGetValue(id, out Button? target)) return;
                Point center = frame.PointFromScreen(target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight)));
                arrow.Margin = new Thickness(Math.Clamp(center.X - 8, 8, Math.Max(8, width - 24)), -7, 0, 0);
            };
            toolbarPopup.Child = frame;
            if (ownsKeyboard) Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (toolbarPopup?.IsOpen != true || toolbarPopup.Child != frame || !ToolbarOwnsForeground()) return;
                Button? restore = rows.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled && (string)button.Tag == focused)
                    ?? rows.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled);
                if (restore is not null) restore.Focus();
                else frame.Focus();
                scroll.ScrollToVerticalOffset(scrollOffset);
            });
        }

        private void CloseToolbarPopover(bool restoreFocus)
        {
            if (toolbarPopover is null) return;
            bool owned = ToolbarOwnsForeground();
            toolbarPopover = null;
            Popup? popup = toolbarPopup;
            toolbarPopup = null;
            if (popup is not null) popup.IsOpen = false;
            Post(new { type = "toolbarPopover", value = false });
            if (!restoreFocus || toolbarClosing || !owned) return;
            ToolbarSetActiveWindow(new WindowInteropHelper(this).Handle);
            if (toolbarReturnFocus != IntPtr.Zero && toolbarReturnFocus != new WindowInteropHelper(this).Handle && ToolbarIsWindow(toolbarReturnFocus)) ToolbarSetFocus(toolbarReturnFocus);
            else webView.Focus();
        }

        private bool ToolbarOwnsForeground()
        {
            IntPtr foreground = ToolbarGetForegroundWindow();
            return foreground == new WindowInteropHelper(this).Handle ||
                (toolbarPopup?.IsOpen == true && toolbarPopup.Child is Visual child && PresentationSource.FromVisual(child) is HwndSource source && foreground == source.Handle);
        }

        private Button ToolbarButton()
        {
            Button button = new Button { Background = Brushes.Transparent, Foreground = toolbarForeground, BorderThickness = new Thickness(0), Cursor = Cursors.Arrow };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(PaddingProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BackgroundProperty, toolbarHover));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(BackgroundProperty, toolbarHover));
            template.Triggers.Add(focus);
            Trigger disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.35));
            template.Triggers.Add(disabled);
            button.Template = template;
            return button;
        }

        private static void ToolbarMiddleText(TextBlock block, string full)
        {
            void Fit()
            {
                int[] characters = System.Globalization.StringInfo.ParseCombiningCharacters(full);
                string text = full;
                for (int count = characters.Length; count > 1; count--)
                {
                    FormattedText measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
                    if (measured.Width <= block.ActualWidth) break;
                    int left = count / 2;
                    int right = (count - 1) / 2;
                    text = full[..characters[left]] + "…" + (right == 0 ? "" : full[characters[characters.Length - right]..]);
                }
                block.Text = text;
            }
            block.Text = full;
            block.SizeChanged += (_, _) => Fit();
            block.Loaded += (_, _) => Fit();
        }

        private static string ToolbarString(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        private static bool ToolbarFlag(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        private static Brush ToolbarBrush(JsonElement item, Brush fallback) => ToolbarString(item, "color") is string color && color.Length > 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : fallback;

        private static FrameworkElement ToolbarStatus(string shape, Brush color)
        {
            string path = shape switch
            {
                "square" => "M2 2H14V14H2Z", "triangle" => "M8 1L15 14H1Z", "diamond" => "M8 0L16 8L8 16L0 8Z",
                "star" => "M8 0L10 5L16 6L12 10L13 16L8 13L3 16L4 10L0 6L6 5Z",
                _ => "M8 1A7 7 0 1 1 7.99 1Z",
            };
            if (shape == "capsule") return new Border { Width = 12, Height = 7, CornerRadius = new CornerRadius(3.5), Background = color, VerticalAlignment = VerticalAlignment.Center };
            return new Path { Data = Geometry.Parse(path), Fill = color, Width = 8, Height = 8, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Center };
        }

        private static FrameworkElement ToolbarGlyph(string name, Brush color, double size)
        {
            Canvas canvas = new Canvas { Width = 16, Height = 16 };
            void Draw(string path, bool fill = false) => canvas.Children.Add(new Path { Data = Geometry.Parse(path), Stroke = fill ? null : color, Fill = fill ? color : null, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            const string rectangle = "M3.5 3H12.5Q14 3 14 4.5V11.5Q14 13 12.5 13H3.5Q2 13 2 11.5V4.5Q2 3 3.5 3Z";
            if (name.StartsWith("split-", StringComparison.Ordinal))
            {
                string state = name[6..];
                bool horizontal = state is "both-horizontal" or "top" or "bottom";
                Draw(rectangle); Draw(horizontal ? "M2 8H14" : "M8 3V13");
                if (state is "both" or "left") Draw("M3.5 4.5H7.1V11.5H3.5Z", true);
                if (state is "both" or "right") Draw("M8.9 4.5H12.5V11.5H8.9Z", true);
                if (state is "both-horizontal" or "top") Draw("M3.5 4.5H12.5V7.1H3.5Z", true);
                if (state is "both-horizontal" or "bottom") Draw("M3.5 8.9H12.5V11.5H3.5Z", true);
            }
            else switch (name)
            {
                case "sidebar": Draw(rectangle); Draw("M6 3V13"); break;
                case "scratch": case "scratch-filled": Draw(rectangle); if (name == "scratch-filled") Draw("M4 5H12V11H4Z", true); break;
                case "overlay": case "overlay-hidden": Draw(rectangle); Draw("M4.5 5.5H11.5V10.5H4.5Z"); if (name == "overlay-hidden") Draw("M2.5 14.5L13.5 1.5"); break;
                case "recent": Draw("M2.8 8A5.2 5.2 0 1 0 4.3 4.3M2.5 2.8V5.2H4.9M8 5.2V8L10 9.3"); break;
                case "bell": case "bell-filled": Draw("M4 11.5V7.3A4 4 0 0 1 12 7.3V11.5L13 12.5H3Z", name == "bell-filled"); Draw("M6.8 14H9.2"); break;
                case "dashboard": Draw("M2 2H6.5V6.5H2ZM9.5 2H14V6.5H9.5ZM2 9.5H6.5V14H2ZM9.5 9.5H14V14H9.5Z"); break;
                case "quick": Draw(rectangle); Draw("M5 6.5L7 8L5 9.5M8.5 10H11"); break;
                case "commands": Draw("M8 2A6 6 0 1 0 8.01 2Z"); Draw("M4.5 8H4.6M8 8H8.1M11.5 8H11.6"); break;
                case "unzoom": Draw("M13.5 2.5L9.5 6.5M9.5 3.5V6.5H12.5M2.5 13.5L6.5 9.5M6.5 12.5V9.5H3.5"); break;
                case "close": Draw("M4 4L12 12M12 4L4 12"); break;
                case "cloud": Draw("M4.5 12.5H11.75A2.75 2.75 0 0 0 12.05 7.02A4 4 0 0 0 4.3 7.6A2.5 2.5 0 0 0 4.5 12.5Z"); break;
            }
            return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr ToolbarGetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "SetActiveWindow")] private static extern IntPtr ToolbarSetActiveWindow(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetFocus")] private static extern IntPtr ToolbarGetFocus();
        [DllImport("user32.dll", EntryPoint = "SetFocus")] private static extern IntPtr ToolbarSetFocus(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool ToolbarIsWindow(IntPtr hwnd);
    }
}
