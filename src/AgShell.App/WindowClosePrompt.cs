using System.Windows;
using System.Windows.Controls;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>
    /// agterm's "Close “name”?" sheet: asked when the user closes a window that has sessions, over that window only. Close
    /// is the default button, as in agterm; Escape cancels. The delete confirms reuse it with their own button.
    /// </summary>
    internal static class WindowClosePrompt
    {
        public static void Show(Window owner, string heading, string text, Action<bool> done, string? confirmLabel = null)
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
            void Answer(bool close)
            {
                if (answered)
                {
                    return;
                }

                answered = true;
                prompt.Close();
                done(close);
            }

            Button cancel = new Button { Content = Strings.T("Отмена"), IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            Button confirm = new Button { Content = confirmLabel ?? Strings.T("Закрыть"), IsDefault = true, MinWidth = 90 };
            cancel.Click += (sender, args) => Answer(false);
            confirm.Click += (sender, args) => Answer(true);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            StackPanel content = new StackPanel { Margin = new Thickness(16), MaxWidth = 520 };
            content.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
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
            confirm.Focus();
        }
    }
}
