using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TTSApp.AI;

namespace TTSApp
{
    public partial class DictionaryWindow : Window
    {
        // Text of the loaded book (selected chapters), used by the AI name scan. Null = no book.
        private readonly string? _bookText;

        public DictionaryWindow(string? bookText = null)
        {
            InitializeComponent();
            _bookText = bookText;
            Loaded += DictionaryWindow_Loaded;
        }

        private void DictionaryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
            BtnAiScan.IsEnabled = AiTextProcessor.IsConfigured && !string.IsNullOrWhiteSpace(_bookText);
            if (!AiTextProcessor.IsConfigured)
                BtnAiScan.ToolTip = "Enable AI Assist and set an API key in Settings to use this.";
            else if (string.IsNullOrWhiteSpace(_bookText))
                BtnAiScan.ToolTip = "Open a book first — the scan reads the loaded chapters.";
        }

        private async void BtnAiScan_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_bookText)) return;
            BtnAiScan.IsEnabled = false;
            string prev = BtnAiScan.Content?.ToString() ?? "AI: Scan book for names";
            BtnAiScan.Content = "Scanning…";
            try
            {
                // Cap the text sent so one scan stays cheap; names recur, so the opening is enough.
                string sample = _bookText!.Length > 40000 ? _bookText.Substring(0, 40000) : _bookText;
                var pairs = await AiTextProcessor.ScanNamesAsync(sample, CancellationToken.None);

                int added = 0;
                foreach (var (name, say) in pairs)
                {
                    if (!AppSettings.PronunciationDict.ContainsKey(name))
                    {
                        AppSettings.PronunciationDict[name] = say;
                        added++;
                    }
                }
                if (added > 0) AppSettings.Save();
                RefreshList();
                MessageBox.Show(
                    pairs.Count == 0
                        ? "No tricky names found in the scanned text."
                        : $"Added {added} new pronunciation(s). Review them in the list and remove any you don't want.",
                    "AI Name Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"AI scan failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnAiScan.Content = prev;
                BtnAiScan.IsEnabled = AiTextProcessor.IsConfigured && !string.IsNullOrWhiteSpace(_bookText);
            }
        }

        private void RefreshList()
        {
            ListDict.ItemsSource = null;
            ListDict.ItemsSource = AppSettings.PronunciationDict.OrderBy(kv => kv.Key).ToList();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var original = TxtOriginal.Text.Trim();
            var replacement = TxtReplacement.Text.Trim();
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(replacement))
            {
                MessageBox.Show("Both fields are required.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AppSettings.PronunciationDict[original] = replacement;
            AppSettings.Save();
            TxtOriginal.Text = "";
            TxtReplacement.Text = "";
            RefreshList();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (ListDict.SelectedItem == null) return;
            dynamic selected = ListDict.SelectedItem;
            string key = selected.Key;
            if (AppSettings.PronunciationDict.ContainsKey(key))
            {
                AppSettings.PronunciationDict.Remove(key);
                AppSettings.Save();
                RefreshList();
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON dictionary|*.json",
                FileName = "pronunciation-dictionary.json"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    AppSettings.PronunciationDict,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(dlg.FileName, json);
                MessageBox.Show($"Exported {AppSettings.PronunciationDict.Count} entries.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON dictionary|*.json|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json = System.IO.File.ReadAllText(dlg.FileName);
                var imported = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
                if (imported == null || imported.Count == 0)
                {
                    MessageBox.Show("No entries found in that file.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                foreach (var kv in imported) AppSettings.PronunciationDict[kv.Key] = kv.Value;
                AppSettings.Save();
                RefreshList();
                MessageBox.Show($"Imported {imported.Count} entries (merged).", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
