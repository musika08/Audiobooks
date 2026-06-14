using System.Windows;
using System.Windows.Controls;

namespace TTSApp
{
    public partial class VoiceTuningWindow : Window
    {
        public VoiceTuningWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => Load();
        }

        private void Load()
        {
            string model = AppSettings.SelectedModel;
            LblEngine.Text = $"Engine: {model}";

            SldTemp.Value = AppSettings.VoiceTemperature;
            SldRep.Value = AppSettings.VoiceRepetitionPenalty;
            SldExag.Value = AppSettings.VoiceExaggeration;
            SldCfgW.Value = AppSettings.VoiceCfgWeight;
            SldCfgS.Value = AppSettings.VoiceCfgScale;

            HookLabel(SldTemp, LblTemp);
            HookLabel(SldRep, LblRep);
            HookLabel(SldExag, LblExag);
            HookLabel(SldCfgW, LblCfgW);
            HookLabel(SldCfgS, LblCfgS);

            // Show only the rows relevant to the selected engine.
            bool xtts = model == "xtts-v2";
            bool chat = model == "chatterbox";
            bool vibe = model == "vibevoice";

            RowTemp.Visibility = (xtts || chat) ? Visibility.Visible : Visibility.Collapsed;
            RowRep.Visibility = xtts ? Visibility.Visible : Visibility.Collapsed;
            RowExag.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            RowCfgW.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            RowCfgS.Visibility = vibe ? Visibility.Visible : Visibility.Collapsed;

            if (xtts) LblHint.Text = "XTTS: lower temperature = steadier; higher = more expressive. Repetition penalty curbs stutters/loops.";
            else if (chat) LblHint.Text = "Chatterbox: exaggeration boosts emotion; CFG weight balances faithfulness vs. expressiveness.";
            else if (vibe) LblHint.Text = "VibeVoice: higher guidance (CFG) sticks closer to the reference voice.";
            else LblHint.Text = "These settings apply to the GPU engines. Select a GPU engine to tune it.";
        }

        private static void HookLabel(Slider s, TextBlock lbl)
        {
            void Update() => lbl.Text = s.Value.ToString("0.0#");
            s.ValueChanged += (_, _) => Update();
            Update();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            SldTemp.Value = 0.7;
            SldRep.Value = 2.0;
            SldExag.Value = 0.5;
            SldCfgW.Value = 0.5;
            SldCfgS.Value = 1.3;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.VoiceTemperature = SldTemp.Value;
            AppSettings.VoiceRepetitionPenalty = SldRep.Value;
            AppSettings.VoiceExaggeration = SldExag.Value;
            AppSettings.VoiceCfgWeight = SldCfgW.Value;
            AppSettings.VoiceCfgScale = SldCfgS.Value;
            AppSettings.Save();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
