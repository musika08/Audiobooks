using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TTSApp
{
    public partial class VoiceCastWindow : Window
    {
        private static readonly (string Label, string Model)[] EngineChoices =
        {
            ("Kokoro Multi-Lang v1.0", "kokoro-multi-lang-v1_0"),
            ("Kokoro Multi-Lang v1.1", "kokoro-multi-lang-v1_1"),
            ("XTTS v2", "xtts-v2"),
            ("Chatterbox", "chatterbox"),
            ("VibeVoice", "vibevoice"),
        };

        private readonly List<SavedVoice> _savedVoices = AppSettings.SavedVoices;
        private bool _loading;

        public VoiceCastWindow()
        {
            InitializeComponent();
            Loaded += VoiceCastWindow_Loaded;
        }

        private static bool IsKokoro(string model) => model.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase);

        private static void FillEngines(ComboBox combo)
        {
            combo.Items.Clear();
            foreach (var (label, model) in EngineChoices)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = model });
        }

        private static string ModelOf(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "kokoro-multi-lang-v1_0";

        private static void SelectModel(ComboBox combo, string model)
        {
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == model) ?? combo.Items[0];
        }

        // Populate a voice dropdown for the given engine model, selecting the saved voice/id.
        private void FillVoices(ComboBox combo, string model, int voiceId, string? cloneRef)
        {
            combo.Items.Clear();
            if (IsKokoro(model))
            {
                foreach (var n in TtsEngine.GetVoiceNamesForModel(model)) combo.Items.Add(n);
                combo.SelectedIndex = voiceId >= 0 && voiceId < combo.Items.Count ? voiceId : 0;
            }
            else if (model == "xtts-v2")
            {
                combo.Items.Add("Built-in default voice");
                foreach (var v in _savedVoices) combo.Items.Add($"Clone: {v.Name}");
                combo.SelectedIndex = SavedIndex(cloneRef) is int si ? si + 1 : 0;
            }
            else // chatterbox / vibevoice — clone only
            {
                if (_savedVoices.Count == 0)
                {
                    combo.Items.Add("(no saved voices — clone one first)");
                    combo.SelectedIndex = 0;
                }
                else
                {
                    foreach (var v in _savedVoices) combo.Items.Add($"Clone: {v.Name}");
                    combo.SelectedIndex = SavedIndex(cloneRef) ?? 0;
                }
            }
        }

        private int? SavedIndex(string? cloneRef)
        {
            if (string.IsNullOrEmpty(cloneRef)) return null;
            int idx = _savedVoices.FindIndex(v => v.FilePath == cloneRef);
            return idx >= 0 ? idx : (int?)null;
        }

        private void VoiceCastWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _loading = true;
            ChkCastEnabled.IsChecked = AppSettings.CastEnabled;

            FillEngines(CmbNarratorEngine);
            FillEngines(CmbDialogueEngine);
            SelectModel(CmbNarratorEngine, AppSettings.CastNarratorModel);
            SelectModel(CmbDialogueEngine, AppSettings.CastDialogueModel);

            FillVoices(CmbNarratorVoice, AppSettings.CastNarratorModel, AppSettings.CastNarratorVoiceId, AppSettings.CastNarratorCloneRef);
            FillVoices(CmbDialogueVoice, AppSettings.CastDialogueModel, AppSettings.CastDialogueVoiceId, AppSettings.CastDialogueCloneRef);
            _loading = false;
        }

        private void CmbNarratorEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbNarratorVoice == null) return;
            FillVoices(CmbNarratorVoice, ModelOf(CmbNarratorEngine), 0, null);
        }

        private void CmbDialogueEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbDialogueVoice == null) return;
            FillVoices(CmbDialogueVoice, ModelOf(CmbDialogueEngine), 0, null);
        }

        // Resolve a role's voice dropdown into (voiceId, cloneRef). Returns false if invalid (clone engine, no clip).
        private bool ResolveRole(string model, ComboBox voiceCombo, out int voiceId, out string? cloneRef)
        {
            voiceId = 0;
            cloneRef = null;
            int idx = voiceCombo.SelectedIndex;
            if (IsKokoro(model))
            {
                voiceId = idx < 0 ? 0 : idx;
                return true;
            }
            if (model == "xtts-v2")
            {
                if (idx <= 0) return true; // built-in default
                cloneRef = _savedVoices[idx - 1].FilePath;
                return true;
            }
            // clone-only engines
            if (_savedVoices.Count == 0) return false;
            cloneRef = _savedVoices[idx < 0 ? 0 : idx].FilePath;
            return true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string nModel = ModelOf(CmbNarratorEngine);
            string dModel = ModelOf(CmbDialogueEngine);

            // Enforce allowed combos: never two different GPU engines.
            bool nGpu = !IsKokoro(nModel), dGpu = !IsKokoro(dModel);
            if (nGpu && dGpu && nModel != dModel)
            {
                MessageBox.Show(
                    "Two different GPU engines can't run together (they'd need two models in VRAM at once).\n\n" +
                    "Use Kokoro + Kokoro, Kokoro + one GPU engine, or the same GPU engine for both roles.",
                    "Voice Cast", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ResolveRole(nModel, CmbNarratorVoice, out int nVoice, out string? nClone))
            {
                MessageBox.Show("The narrator engine needs a cloned voice. Clone one first (🎤), then pick it here.",
                    "Voice Cast", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ResolveRole(dModel, CmbDialogueVoice, out int dVoice, out string? dClone))
            {
                MessageBox.Show("The dialogue engine needs a cloned voice. Clone one first (🎤), then pick it here.",
                    "Voice Cast", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppSettings.CastEnabled = ChkCastEnabled.IsChecked == true;
            AppSettings.CastNarratorModel = nModel;
            AppSettings.CastNarratorVoiceId = nVoice;
            AppSettings.CastNarratorCloneRef = nClone;
            AppSettings.CastDialogueModel = dModel;
            AppSettings.CastDialogueVoiceId = dVoice;
            AppSettings.CastDialogueCloneRef = dClone;
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
