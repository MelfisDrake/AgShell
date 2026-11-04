using System.Windows;
using System.Windows.Controls;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>
    /// agterm's question before a page hands an address to the browser. It does not block the window, so it can be taken
    /// away when its page leaves the screen, and Cancel is the default: Enter typed for the page cannot approve it.
    /// </summary>
    internal static class LinkPrompt
    {
        /// <summary>Shows the question over <paramref name="owner"/>; <paramref name="done"/> gets the answer once. Returns what dismisses it.</summary>
        public static Action Show(Window owner, Uri url, Action<bool> done)
        {
            bool answered = false;
            Window prompt = new Window
            {
                Owner = owner,
                Title = "AgShell",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            void Answer(bool approved)
            {
                if (answered)
                {
                    return;
                }

                answered = true;
                prompt.Close();
                done(approved);
            }

            Button cancel = new Button { Content = Strings.T("Отмена"), IsDefault = true, IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            Button open = new Button { Content = Strings.T("Открыть"), MinWidth = 90 };
            cancel.Click += (sender, args) => Answer(false);
            open.Click += (sender, args) => Answer(true);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(open);
            StackPanel content = new StackPanel { Margin = new Thickness(16), MaxWidth = 520 };
            content.Children.Add(new TextBlock { Text = Strings.T("Открыть {0} в браузере?", url.GetLeftPart(UriPartial.Authority)), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = url.AbsoluteUri, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(buttons);
            prompt.Content = content;
            prompt.Closed += (sender, args) =>
            {
                if (!answered)
                {
                    answered = true;
                    done(false);
                }
            };
            prompt.Show();
            cancel.Focus();
            return () =>
            {
                if (!answered)
                {
                    answered = true;
                    prompt.Close();
                }
            };
        }
    }
}
