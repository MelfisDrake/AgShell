using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>
    /// agterm's DockBadgeController: the app-wide unseen total, the sessions' counts summed over the open windows, capped at 99
    /// and gone at zero or with "Show notification badges" off. Windows draws no count on an unpackaged app's icon, so it is
    /// an overlay on the taskbar button, drawn here, and the same on every open window's button, as the Dock has one icon.
    /// </summary>
    internal sealed partial class WindowLibrary
    {
        private bool badgeQueued;
        private int? badgeShown;

        /// <summary>Coalesces every change of a pass into one recount, as agterm's observation does.</summary>
        internal void ScheduleBadge()
        {
            if (badgeQueued)
            {
                return;
            }

            badgeQueued = true;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                badgeQueued = false;
                RefreshBadge(force: false);
            });
        }

        private void RefreshBadge(bool force)
        {
            int count = Settings.NotificationBadgeEnabled == false ? 0 : Math.Min(UnseenTotal(open.Select(window => window.Model)), 99);
            if (!force && count == badgeShown)
            {
                return;
            }

            badgeShown = count;
            ImageSource? overlay = count == 0 ? null : BadgeImage(count);
            foreach (MainWindow window in open)
            {
                window.TaskbarItemInfo ??= new TaskbarItemInfo();
                window.TaskbarItemInfo.Overlay = overlay;
            }
        }

        /// <summary>A window opened later gets the overlay already shown.</summary>
        private void ApplyBadge()
        {
            RefreshBadge(force: true);
        }

        internal static int UnseenTotal(IEnumerable<AppModel> models)
        {
            return models.Sum(model => model.AllSessions.Sum(session => session.UnseenCount));
        }

        /// <summary>A system-red disc with the count in white, as macOS draws the Dock badge.</summary>
        private static ImageSource BadgeImage(int count)
        {
            string text = count.ToString(CultureInfo.InvariantCulture);
            FormattedText label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), text.Length > 1 ? 9 : 11, Brushes.White, 1.0);
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), null, new Point(8, 8), 8, 8);
                context.DrawText(label, new Point(8 - label.Width / 2, 8 - label.Height / 2));
            }

            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
