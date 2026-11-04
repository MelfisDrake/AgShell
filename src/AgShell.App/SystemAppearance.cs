using System.Windows;
using AgShell.Core;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace AgShell.App
{
    /// <summary>
    /// agterm's SystemAppearanceObserver: one watcher for the whole app, started once, that tells the open windows when the
    /// Windows mode, Transparency effects or the accent color change; a window listens while it is open.
    /// </summary>
    public sealed class SystemAppearance
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private readonly UISettings uiSettings = new();

        public SystemAppearance()
        {
            Dark = ReadDark();
            Transparency = ReadTransparency();
            Accent = ReadAccent();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        /// <summary>Whether apps show dark, as agterm takes NSApp's effective appearance.</summary>
        public bool Dark { get; private set; }

        /// <summary>agterm's Reduce Transparency the other way round: Transparency effects on in the Windows settings.</summary>
        public bool Transparency { get; private set; }

        /// <summary>agterm's Color.accentColor with no accent of its own in the assets: the accent color Windows is set to.</summary>
        public string Accent { get; private set; }

        /// <summary>The mode or Transparency effects changed: the windows resolve their theme and backdrop again.</summary>
        public event Action? AppearanceChanged;

        /// <summary>A new accent repaints the selections, as AppKit redraws accentColor in agterm.</summary>
        public event Action? AccentChanged;

        private static bool ReadDark()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return Themes.AppsDark(key?.GetValue("AppsUseLightTheme"));
        }

        private static bool ReadTransparency()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }

        private string ReadAccent()
        {
            global::Windows.UI.Color accent = uiSettings.GetColorValue(UIColorType.Accent);
            return $"#{accent.R:x2}{accent.G:x2}{accent.B:x2}";
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General)
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                bool dark = ReadDark();
                bool transparency = ReadTransparency();
                if (dark == Dark && transparency == Transparency)
                {
                    return;
                }

                Dark = dark;
                Transparency = transparency;
                AppearanceChanged?.Invoke();
            });
        }

        private void OnColorValuesChanged(UISettings sender, object args)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                string accent = ReadAccent();
                if (accent == Accent)
                {
                    return;
                }

                Accent = accent;
                AccentChanged?.Invoke();
            });
        }
    }
}
