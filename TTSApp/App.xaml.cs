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
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(120, 120, 120));
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(16, 124, 16));
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(106, 13, 173));
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(230, 230, 230));
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(200, 220, 255));
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            }
            else if (themeName == "Midnight")
            {
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(10, 10, 20));
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(20, 20, 40));
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(15, 15, 30));
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(220, 220, 255));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(160, 160, 200));
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(100, 100, 140));
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(40, 40, 60));
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(80, 160, 255));
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(20, 180, 80));
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(160, 100, 255));
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(35, 35, 55));
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(45, 45, 70));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(40, 60, 100));
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(30, 30, 50));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(18, 18, 35));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(25, 25, 45));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(40, 40, 65));
            }
            else // Dark
            {
                resources["BrushWindowBg"] = new SolidColorBrush(Color.FromRgb(26, 26, 26));
                resources["BrushPanelBg"] = new SolidColorBrush(Color.FromRgb(45, 45, 45));
                resources["BrushPanelBgAlt"] = new SolidColorBrush(Color.FromRgb(37, 37, 37));
                resources["BrushTextPrimary"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                resources["BrushTextMuted"] = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                resources["BrushBorder"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                resources["BrushAccentBlue"] = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                resources["BrushAccentGreen"] = new SolidColorBrush(Color.FromRgb(16, 124, 16));
                resources["BrushAccentPurple"] = new SolidColorBrush(Color.FromRgb(106, 13, 173));
                resources["BrushButtonBg"] = new SolidColorBrush(Color.FromRgb(74, 74, 74));
                resources["BrushButtonHover"] = new SolidColorBrush(Color.FromRgb(85, 85, 85));
                resources["BrushListSelected"] = new SolidColorBrush(Color.FromRgb(61, 90, 128));
                resources["BrushListHover"] = new SolidColorBrush(Color.FromRgb(53, 53, 53));
                resources["BrushMenuBg"] = new SolidColorBrush(Color.FromRgb(37, 37, 37));
                resources["BrushDropdownBg"] = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                resources["BrushDropdownHover"] = new SolidColorBrush(Color.FromRgb(65, 65, 65));
            }
        }
    }
}
