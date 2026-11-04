using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AgShell.App.Tests
{
    public class HudFloatTests
    {
        [Fact]
        public void PassiveLinkHitUsesItsTextBoundsWithoutMouseEvents()
        {
            Exception? failure = null;
            Thread thread = new Thread(() =>
            {
                try
                {
                    TextBlock line = new TextBlock { FontSize = 20 };
                    Hyperlink link = new Hyperlink(new Run("safe link"));
                    Hyperlink other = new Hyperlink(new Run("other link"));
                    line.Inlines.Add(new Run("prefix "));
                    line.Inlines.Add(link);
                    line.Inlines.Add(new Run(" gap "));
                    line.Inlines.Add(other);
                    line.Measure(new Size(800, 100));
                    line.Arrange(new Rect(0, 0, 800, 100));
                    line.UpdateLayout();

                    FormattedText prefix = Measure("prefix ", line);
                    FormattedText label = Measure("safe link", line);
                    Point center = new Point(prefix.WidthIncludingTrailingWhitespace + label.Width / 2, label.Height / 2);
                    Assert.False(line.IsMouseOver);
                    Assert.True(HudFloat.LinkContains(line, link, center));
                    Assert.False(HudFloat.LinkContains(line, other, center));
                    Assert.False(HudFloat.LinkContains(line, link, new Point(0, center.Y)));
                    Assert.False(HudFloat.LinkContains(line, link, new Point(center.X, label.Height + 5)));
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        private static FormattedText Measure(string text, TextBlock line)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch), line.FontSize, Brushes.Black, 1);
        }
    }
}
