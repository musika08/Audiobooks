using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TTSApp
{
    public partial class DictionaryWindow : Window
    {
        public DictionaryWindow()
        {
            InitializeComponent();
            Loaded += DictionaryWindow_Loaded;
        }

        private void DictionaryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
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
