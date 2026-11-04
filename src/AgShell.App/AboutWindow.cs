using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>agterm's About panel: the icon, the name, the version with its short commit and a link to the project.</summary>
    internal static class AboutWindow
    {
        private static Window? shown;

        public static void Show(Window owner, string informationalVersion)
        {
            if (shown is not null)
            {
                shown.Activate();
                return;
            }

            (string version, string? commit) = AppVersion.Split(informationalVersion);
            Window about = new Window
            {
                Owner = owner,
                Title = Strings.T("О программе AgShell"),
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            StackPanel content = new StackPanel { Margin = new Thickness(32, 20, 32, 16), MinWidth = 260 };
            content.Children.Add(new Image
            {
                Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/agshell.ico")),
                Width = 64,
                Height = 64,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock { Text = "AgShell", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            content.Children.Add(new TextBlock
            {
                Text = commit is null ? Strings.T("Версия {0}", version) : Strings.T("Версия {0} ({1})", version, commit),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });
            Hyperlink link = new Hyperlink(new Run(AppVersion.RepositoryUrl)) { NavigateUri = new Uri(AppVersion.RepositoryUrl) };
            link.RequestNavigate += (sender, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            content.Children.Add(new TextBlock(link) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            Button ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            ok.Click += (sender, args) => about.Close();
            content.Children.Add(ok);
            about.Content = content;
            about.Closed += (sender, args) => shown = null;
            shown = about;
            about.Show();
            ok.Focus();
        }
    }
}
