using System;
using System.Windows;
using System.Windows.Media;

namespace TTSApp
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            ThemeManager.ApplyTheme("Dark");
            Exit += (_, _) => PythonSidecarEngine.ShutdownServer();
        }
    }

    public static class ThemeManager
    {
        public static string CurrentTheme { get; private set; } = "Dark";

        public static void ApplyTheme(string themeName)
        {
            CurrentTheme = themeName;
            var resources = Application.Current.Resources;

            if (themeName == "Light")
            {
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(244, 244, 245));   // zinc-100
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));     // white card
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(250, 250, 251));
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(82, 82, 91));   // zinc-600
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(113, 113, 122));    // zinc-500
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(228, 228, 231));       // zinc-200
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(37, 99, 235));     // blue-600
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(22, 163, 74));    // green-600
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(124, 58, 237));  // violet-600
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(228, 228, 231));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(220, 252, 231)); // green tint
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(244, 244, 245));
            }
            else if (themeName == "Midnight")
            {
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(8, 11, 22));         // deep navy-black
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(17, 22, 41));         // elevated navy card
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(12, 16, 31));
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(226, 232, 255));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(148, 163, 199));
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(100, 116, 152));
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(30, 39, 66));
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(167, 139, 250));
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(28, 36, 60));
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(38, 48, 78));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(30, 45, 75));
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(20, 27, 48));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(12, 16, 31));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(17, 22, 41));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(30, 39, 66));
            }
            else // Dark — near-black surfaces, elevated charcoal cards, vibrant green accent
            {
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(10, 10, 11));      // near-black
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(24, 24, 27));        // elevated card (zinc-900)
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(18, 18, 20));     // recessed surface
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(161, 161, 170)); // zinc-400
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(113, 113, 122));   // zinc-500
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(39, 39, 42));         // zinc-800
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(59, 130, 246));   // blue-500
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(34, 197, 94));   // emerald-500 (image)
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(139, 92, 246)); // violet-500
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(39, 39, 42));       // subtle chip
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(63, 63, 70));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(39, 39, 46));
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(30, 30, 33));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(16, 16, 18));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(39, 39, 42));
            }
        }
    }
}
