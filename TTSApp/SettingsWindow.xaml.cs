using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TTSApp
{
    public partial class SettingsWindow : Window
    {
        // Remembered scroll position so reopening Settings lands where you left off.
        private static double _lastScrollOffset;

        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtRewind.Text = AppSettings.RewindSeconds.ToString();
            TxtForward.Text = AppSettings.ForwardSeconds.ToString();
            ChkAnnounce.IsChecked = AppSettings.AnnounceChapterTitle;
            ChkDialogMode.IsChecked = AppSettings.EnableDialogMode;
            ChkLevelVolume.IsChecked = AppSettings.LevelSegmentVolume;
            ChkDereverb.IsChecked = AppSettings.DereverbCloned;
            ChkMerge.IsChecked = AppSettings.MergeIntoSingleFile;
            CmbNormMode.SelectedIndex = AppSettings.NormalizationMode;
            TxtTargetLufs.Text = AppSettings.TargetLufs.ToString();
            ChkTrimSilence.IsChecked = AppSettings.TrimSilence;
            TxtIntroPath.Text = AppSettings.IntroAudioPath ?? "";
            TxtOutroPath.Text = AppSettings.OutroAudioPath ?? "";
            TxtBackgroundPath.Text = AppSettings.BackgroundAudioPath ?? "";
            TxtBgVolume.Text = AppSettings.BackgroundVolumePercent.ToString();
            CmbExportPreset.SelectedItem = CmbExportPreset.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => (i.Content.ToString() ?? "").StartsWith(AppSettings.ExportPreset))
                ?? CmbExportPreset.Items[0];
            TxtCommaPause.Text = AppSettings.PauseAfterCommaMs.ToString();
            TxtSentencePause.Text = AppSettings.PauseAfterSentenceMs.ToString();
            TxtEllipsisPause.Text = AppSettings.PauseAfterEllipsisMs.ToString();
            TxtParagraphPause.Text = AppSettings.PauseAfterParagraphMs.ToString();
            SliderPauseScale.Value = AppSettings.PauseScalePercent;
            LblPauseScale.Text = $"{AppSettings.PauseScalePercent}%";

            CmbTheme.SelectedItem = CmbTheme.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Content.ToString() == AppSettings.Theme);

            LblVersion.Text = $"Current: v{Updater.AppVersion}";

            // Populate dialog voice dropdown with Kokoro Multi-Lang v1.0 voices (dialog mode is Kokoro-only).
            CmbDialogVoice.Items.Clear();
            foreach (var n in TtsEngine.GetKokoroV1_0VoiceNames()) CmbDialogVoice.Items.Add(n);
            if (AppSettings.DialogVoiceId >= 0 && AppSettings.DialogVoiceId < CmbDialogVoice.Items.Count)
                CmbDialogVoice.SelectedIndex = AppSettings.DialogVoiceId;
            else
                CmbDialogVoice.SelectedIndex = Math.Min(1, CmbDialogVoice.Items.Count - 1);

            // Restore scroll after layout settles.
            Dispatcher.BeginInvoke(new Action(() => SettingsScroll.ScrollToVerticalOffset(_lastScrollOffset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtRewind.Text, out int rewind) && rewind > 0)
                AppSettings.RewindSeconds = rewind;
            if (int.TryParse(TxtForward.Text, out int forward) && forward > 0)
                AppSettings.ForwardSeconds = forward;
            if (int.TryParse(TxtCommaPause.Text, out int commaPause) && commaPause >= 0)
                AppSettings.PauseAfterCommaMs = commaPause;
            if (int.TryParse(TxtSentencePause.Text, out int sentPause) && sentPause >= 0)
                AppSettings.PauseAfterSentenceMs = sentPause;
            if (int.TryParse(TxtEllipsisPause.Text, out int ellipsisPause) && ellipsisPause >= 0)
                AppSettings.PauseAfterEllipsisMs = ellipsisPause;
            if (int.TryParse(TxtParagraphPause.Text, out int paraPause) && paraPause >= 0)
                AppSettings.PauseAfterParagraphMs = paraPause;
            AppSettings.PauseScalePercent = (int)SliderPauseScale.Value;

            AppSettings.AnnounceChapterTitle = ChkAnnounce.IsChecked == true;
            AppSettings.EnableDialogMode = ChkDialogMode.IsChecked == true;
            AppSettings.LevelSegmentVolume = ChkLevelVolume.IsChecked == true;
            AppSettings.DereverbCloned = ChkDereverb.IsChecked == true;
            AppSettings.MergeIntoSingleFile = ChkMerge.IsChecked == true;
            AppSettings.NormalizationMode = CmbNormMode.SelectedIndex < 0 ? 0 : CmbNormMode.SelectedIndex;
            AppSettings.NormalizeAudio = AppSettings.NormalizationMode != 0;
            if (double.TryParse(TxtTargetLufs.Text, out double lufs)) AppSettings.TargetLufs = lufs;
            AppSettings.TrimSilence = ChkTrimSilence.IsChecked == true;
            AppSettings.IntroAudioPath = string.IsNullOrWhiteSpace(TxtIntroPath.Text) ? null : TxtIntroPath.Text;
            AppSettings.OutroAudioPath = string.IsNullOrWhiteSpace(TxtOutroPath.Text) ? null : TxtOutroPath.Text;
            AppSettings.BackgroundAudioPath = string.IsNullOrWhiteSpace(TxtBackgroundPath.Text) ? null : TxtBackgroundPath.Text;
            if (int.TryParse(TxtBgVolume.Text, out int bgvol) && bgvol >= 0 && bgvol <= 100)
                AppSettings.BackgroundVolumePercent = bgvol;
            if (CmbExportPreset.SelectedItem is ComboBoxItem presetItem)
                AppSettings.ExportPreset = (presetItem.Content.ToString() ?? "Custom").Split(' ')[0];
            AppSettings.DialogVoiceId = CmbDialogVoice.SelectedIndex;

            if (CmbTheme.SelectedItem is ComboBoxItem item)
            {
                AppSettings.Theme = item.Content.ToString() ?? "Dark";
                ThemeManager.ApplyTheme(AppSettings.Theme);
            }
            AppSettings.Save();
            _lastScrollOffset = SettingsScroll.VerticalOffset;
            DialogResult = true;
            Close();
        }

        // #17 — applying a preset fills in the loudness/trim controls.
        private void CmbExportPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbExportPreset.SelectedItem is not ComboBoxItem item) return;
            string name = item.Content.ToString() ?? "";
            // Guard: controls may not exist yet during initial load.
            if (CmbNormMode == null) return;

            if (name.StartsWith("ACX"))
            {
                CmbNormMode.SelectedIndex = 3; // LUFS
                TxtTargetLufs.Text = "-20";
                ChkTrimSilence.IsChecked = true;
            }
            else if (name.StartsWith("Podcast"))
            {
                CmbNormMode.SelectedIndex = 3;
                TxtTargetLufs.Text = "-16";
                ChkTrimSilence.IsChecked = true;
            }
            else if (name.StartsWith("Plain"))
            {
                CmbNormMode.SelectedIndex = 0; // Off
                ChkTrimSilence.IsChecked = false;
            }
            // "Custom" leaves the controls as-is.
        }

        private void SliderPauseScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblPauseScale != null)
                LblPauseScale.Text = $"{(int)e.NewValue}%";
        }

        private static string? PickAudioFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select audio file",
                Filter = "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.ogg|All files|*.*"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private void BtnBrowseIntro_Click(object sender, RoutedEventArgs e) { var f = PickAudioFile(); if (f != null) TxtIntroPath.Text = f; }
        private void BtnClearIntro_Click(object sender, RoutedEventArgs e) => TxtIntroPath.Text = "";
        private void BtnBrowseOutro_Click(object sender, RoutedEventArgs e) { var f = PickAudioFile(); if (f != null) TxtOutroPath.Text = f; }
        private void BtnClearOutro_Click(object sender, RoutedEventArgs e) => TxtOutroPath.Text = "";
        private void BtnBrowseBackground_Click(object sender, RoutedEventArgs e) { var f = PickAudioFile(); if (f != null) TxtBackgroundPath.Text = f; }
        private void BtnClearBackground_Click(object sender, RoutedEventArgs e) => TxtBackgroundPath.Text = "";

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = "Checking...";
            try { await Updater.CheckForUpdatesAsync(); }
            finally { BtnCheckUpdate.Content = "Check for Updates"; BtnCheckUpdate.IsEnabled = true; }
        }

        private async void BtnSyncSidecar_Click(object sender, RoutedEventArgs e)
        {
            BtnSyncSidecar.IsEnabled = false;
            BtnSyncSidecar.Content = "Syncing...";
            try { await Updater.SyncSidecarFromGitHubAsync(); }
            finally { BtnSyncSidecar.Content = "Sync GPU Sidecar Files"; BtnSyncSidecar.IsEnabled = true; }
        }

        private void BtnResetGpu_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                "Delete the GPU engine Python environments so they reinstall from scratch?\n\n" +
                "Downloaded model weights are kept. The next time you pick a GPU engine it will reinstall (several minutes).",
                "Reset GPU Engines", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var (venvs, runtime) = PythonSidecarEngine.ResetEnvironment();
            MessageBox.Show(
                $"Removed {venvs} engine environment(s){(runtime ? " + the bundled Python" : "")}.\n\n" +
                "Pick a GPU engine to reinstall.",
                "Reset GPU Engines", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _lastScrollOffset = SettingsScroll.VerticalOffset;
            DialogResult = false;
            Close();
        }
    }
}
