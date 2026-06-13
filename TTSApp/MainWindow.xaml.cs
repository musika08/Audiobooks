using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using NAudio.Lame;
using NAudio.Wave;

namespace TTSApp
{
    public partial class MainWindow : Window
    {
        // Custom commands for the Edit menu shortcuts that have no WPF built-in.
        public static readonly RoutedUICommand UndoAllCommand = new("Undo All", "UndoAll", typeof(MainWindow));
        public static readonly RoutedUICommand RedoAllCommand = new("Redo All", "RedoAll", typeof(MainWindow));
        public static readonly RoutedUICommand ReplaceAllCommand = new("Replace All", "ReplaceAll", typeof(MainWindow));

        private List<ChapterItem> _chapters = new();
        private ITtsEngine _ttsEngine = new TtsEngine();
        private bool _isUpdatingText = false;
        private readonly Stack<List<ChapterItem>> _undoStack = new();
        private readonly Stack<List<ChapterItem>> _redoStack = new();
        private readonly List<string> _recentFiles = new();
        private const int MaxRecentFiles = 10;

        // Audio player
        private WaveOutEvent? _waveOut;
        private WaveFileReader? _waveReader;
        private DispatcherTimer? _playbackTimer;
        private string? _currentPreviewFile;

        // Auto-save
        private DispatcherTimer? _autoSaveTimer;
        private string? _currentProjectPath;

        // Cover image
        private string? _coverImagePath;

        // Mini player
        private MiniPlayerWindow? _miniPlayer;

        // Debounce for the expensive all-chapters duration recalc (hot paths: typing, checkboxes).
        private DispatcherTimer? _durationDebounce;

        // Set while a conversion is running; re-clicking Convert cancels it.
        private CancellationTokenSource? _convertCts;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AppSettings.Load();
            CleanStaleTempFiles();
            PythonSidecarEngine.CleanupStaleServer();
            LoadRecentFiles();
            ApplyMenuThemeCheck(AppSettings.Theme);
            ThemeManager.ApplyTheme(AppSettings.Theme);
            ChkMerge.IsChecked = AppSettings.MergeIntoSingleFile;
            StartAutoSaveTimer();
            TryRestoreAutoSave();
            LoadCoverImage();

            if (!ModelDownloader.IsModelReady())
            {
                var result = MessageBox.Show(
                    "The Kokoro TTS model (~80 MB) is required but not found.\n\nDownload it now?",
                    "Model Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Downloading model...");
                    var progress = new Progress<double>(p =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = p * 100;
                            TxtStatus.Text = $"Downloading model... {p:P0}";
                        });
                    });

                    try
                    {
                        await ModelDownloader.DownloadAsync(progress);
                        MessageBox.Show("Model downloaded successfully!", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to download model:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    finally
                    {
                        SetBusy(false, "Ready");
                    }
                }
            }

            // Never auto-launch a GPU sidecar engine on startup (slow, may download GB). Boot on Kokoro;
            // the user re-picks a GPU engine when they want it. This also avoids a dropdown/engine mismatch
            // where the saved sidecar selection would leave the engine uninitialized.
            if (IsSidecarModel(AppSettings.SelectedModel))
            {
                AppSettings.SelectedModel = "kokoro-multi-lang-v1_0";
                AppSettings.Save();
            }

            // Set model dropdown to saved model
            for (int i = 0; i < CmbModel.Items.Count; i++)
            {
                if (CmbModel.Items[i] is ComboBoxItem item && item.Tag?.ToString() == AppSettings.SelectedModel)
                {
                    CmbModel.SelectedIndex = i;
                    break;
                }
            }

            if (ModelDownloader.IsModelReady())
            {
                InitializeTts("cpu");
            }

            UpdateGpuEngineAvailability();
        }

        // Gray out GPU sidecar engines in the dropdown when no Nvidia GPU is detected.
        private void UpdateGpuEngineAvailability()
        {
            bool hasGpu = HasNvidiaGpu();
            foreach (var obj in CmbModel.Items)
            {
                if (obj is ComboBoxItem ci && ci.Tag?.ToString() is string tag && IsSidecarModel(tag))
                {
                    ci.IsEnabled = hasGpu;
                    if (!hasGpu && !ci.Content.ToString()!.Contains("no GPU"))
                        ci.Content = $"{ci.Content}  — no GPU";
                }
            }
        }

        private static bool HasNvidiaGpu()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "-L",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(4000);
                return p.HasExited && p.ExitCode == 0;
            }
            catch { return false; }
        }

        // Remove leftover temp audio from crashed/previous runs.
        private static void CleanStaleTempFiles()
        {
            try
            {
                string tmp = Path.GetTempPath();
                string[] patterns = { "tts_preview_*.wav", "tts_chunk_*.wav", "tts_silence_*.wav",
                                      "tts_merge_*.wav", "tts_merged_*.wav", "norm_*.wav", "loud_*.wav", "ffmeta_*.txt" };
                foreach (var pat in patterns)
                    foreach (var f in Directory.GetFiles(tmp, pat))
                    {
                        // Only delete files older than 1 hour to avoid touching a concurrent instance.
                        try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f); } catch { }
                    }
            }
            catch { /* ignore */ }
        }

        private void InitializeTts(string provider)
        {
            try
            {
                _ttsEngine.Initialize(provider);
                CmbVoice.Items.Clear();
                var names = _ttsEngine.GetSpeakerNames();
                foreach (var name in names)
                {
                    CmbVoice.Items.Add(name);
                }
                if (CmbVoice.Items.Count > 0) CmbVoice.SelectedIndex = 0;
                PopulateChapterVoices();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize TTS engine:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Mirror the voice list into the per-chapter override dropdown (index 0 = use global voice).
        private void PopulateChapterVoices()
        {
            CmbChapterVoice.Items.Clear();
            CmbChapterVoice.Items.Add("(Use default voice)");
            foreach (var name in CmbVoice.Items) CmbChapterVoice.Items.Add(name);
            CmbChapterVoice.SelectedIndex = 0;
        }

        private void BtnPrevChapter_Click(object sender, RoutedEventArgs e) => StepChapter(-1);

        private async void BtnNextChapter_Click(object sender, RoutedEventArgs e)
        {
            int idx = ChaptersList.SelectedIndex;
            bool atEnd = idx >= 0 && idx == ChaptersList.Items.Count - 1;
            // At the end of a URL-imported chapter → fetch the site's next page and append it.
            if (atEnd && ChaptersList.SelectedItem is ChapterItem cur && !string.IsNullOrEmpty(cur.NextUrl))
            {
                await ImportUrlAsChapterAsync(cur.NextUrl);
                return;
            }
            StepChapter(1);
        }

        private void StepChapter(int delta)
        {
            if (ChaptersList.Items.Count == 0) return;
            int idx = ChaptersList.SelectedIndex < 0 ? (delta > 0 ? -1 : 0) : ChaptersList.SelectedIndex;
            int target = Math.Clamp(idx + delta, 0, ChaptersList.Items.Count - 1);
            if (target == ChaptersList.SelectedIndex) return;
            ChaptersList.SelectedIndex = target;
            ChaptersList.ScrollIntoView(ChaptersList.SelectedItem);
        }

        private void BtnTidy_Click(object sender, RoutedEventArgs e)
        {
            string before = TxtPreview.Text;
            if (string.IsNullOrWhiteSpace(before)) { TxtStatus.Text = "Nothing to tidy"; return; }

            SaveStateForUndo();
            string after = TextCleaner.TidyContent(before);
            // Setting the text fires TextChanged → persists into the selected chapter's Content.
            TxtPreview.Text = after;
            TxtStatus.Text = before == after
                ? "Already tidy (no changes needed)"
                : $"Tidied: {before.Length} → {after.Length} chars";
        }

        private void BtnTidyAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = _chapters.Where(c => c.IsSelected).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("No checked chapters to tidy.", "Tidy All", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"Tidy {targets.Count} checked chapter(s)? This rewrites their text.",
                "Tidy All", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            SaveStateForUndo();
            foreach (var c in targets) c.Content = TextCleaner.TidyContent(c.Content);

            // Refresh the editor if the current chapter was tidied.
            if (ChaptersList.SelectedItem is ChapterItem cur && cur.IsSelected)
            {
                _isUpdatingText = true;
                TxtPreview.Text = cur.Content;
                _isUpdatingText = false;
            }
            TxtStatus.Text = $"Tidied {targets.Count} chapter(s)";
            ScheduleDurationRefresh();
        }

        private void CmbChapterVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingText) return;
            if (ChaptersList.SelectedItem is ChapterItem ch)
                ch.VoiceOverride = CmbChapterVoice.SelectedIndex - 1;
        }

        // GPU-only engines run through the Python sidecar; everything else is in-process Kokoro.
        private static bool IsSidecarModel(string modelName) =>
            modelName is "xtts-v2" or "chatterbox" or "fish-opus";

        private static ITtsEngine CreateEngine(string modelName) =>
            IsSidecarModel(modelName) ? new PythonSidecarEngine(modelName) : new TtsEngine();

        private void SelectModelInDropdown(string modelName)
        {
            for (int i = 0; i < CmbModel.Items.Count; i++)
            {
                if (CmbModel.Items[i] is ComboBoxItem it && it.Tag?.ToString() == modelName)
                {
                    CmbModel.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ResetCloneButton()
        {
            // Mic icon stays; inactive state shown by the default button background.
            BtnCloneVoice.Background = (System.Windows.Media.Brush)FindResource("BrushButtonBg");
            CmbVoice.IsEnabled = true;
        }

        private void BtnCloneVoice_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: clear an active clone, otherwise pick a reference file.
            if (!string.IsNullOrEmpty(AppSettings.CloneReferencePath))
            {
                AppSettings.CloneReferencePath = null;
                ResetCloneButton();
                TxtStatus.Text = "Voice cloning off — using built-in voice";
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select reference audio for voice cloning",
                Filter = "Audio files|*.wav;*.mp3;*.flac;*.m4a;*.ogg|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            AppSettings.CloneReferencePath = dlg.FileName;
            // Active state shown by accent background.
            BtnCloneVoice.Background = (System.Windows.Media.Brush)FindResource("BrushAccentPurple");
            CmbVoice.IsEnabled = false; // cloned voice overrides the dropdown
            TxtStatus.Text = $"Voice cloning: {System.IO.Path.GetFileName(dlg.FileName)}";
        }

        private async void CmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbModel.SelectedItem is not ComboBoxItem item) return;
            string newModel = item.Tag?.ToString() ?? "kokoro-multi-lang-v1_0";
            if (newModel == AppSettings.SelectedModel) return;

            AppSettings.SelectedModel = newModel;
            AppSettings.Save();

            _ttsEngine.Dispose();
            _ttsEngine = CreateEngine(newModel);

            // Voice cloning only applies to GPU sidecar engines; reset it on every model switch.
            AppSettings.CloneReferencePath = null;
            ResetCloneButton();
            BtnCloneVoice.Visibility = IsSidecarModel(newModel) ? Visibility.Visible : Visibility.Collapsed;

            // GPU sidecar engine: start server off the UI thread, then fill the voice list.
            if (IsSidecarModel(newModel))
            {
                string label = (item.Content?.ToString() ?? newModel);

                // #25 — first-run wizard: warn about the one-time multi-GB setup before starting.
                if (PythonSidecarEngine.NeedsFirstRunSetup(newModel))
                {
                    var go = MessageBox.Show(
                        $"First-time setup for {label}.\n\n" +
                        "This will (one time):\n" +
                        "  • install a private Python environment (if needed)\n" +
                        "  • download PyTorch + the TTS model — several GB\n\n" +
                        "It can take 10–20 minutes. Progress shows in the status bar and python\\setup.log.\n\n" +
                        "Continue?",
                        "GPU Engine Setup", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (go != MessageBoxResult.OK)
                    {
                        AppSettings.SelectedModel = "kokoro-multi-lang-v1_0";
                        AppSettings.Save();
                        _ttsEngine.Dispose();
                        _ttsEngine = new TtsEngine();
                        InitializeTts("cpu");
                        SelectModelInDropdown("kokoro-multi-lang-v1_0");
                        return;
                    }
                }
                SetBusy(true, $"Starting {label} (GPU)... first run installs Python deps + downloads weights.");
                PythonSidecarEngine.StatusCallback = msg => Dispatcher.Invoke(() => TxtStatus.Text = msg);
                PythonSidecarEngine.ProgressCallback = frac => Dispatcher.Invoke(() =>
                {
                    if (frac is double f)
                    {
                        ProgressBar.IsIndeterminate = false;
                        ProgressBar.Value = f * 100;
                    }
                    else
                    {
                        ProgressBar.IsIndeterminate = true;
                    }
                });
                try
                {
                    await Task.Run(() => _ttsEngine.Initialize("cuda"));
                    CmbVoice.Items.Clear();
                    foreach (var name in _ttsEngine.GetSpeakerNames())
                        CmbVoice.Items.Add(name);
                    if (CmbVoice.Items.Count > 0) CmbVoice.SelectedIndex = 0;
                    PopulateChapterVoices();
                    TxtStatus.Text = $"{label} ready";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Could not start the GPU ({label}) engine:\n{ex.Message}\n\nFalling back to Kokoro English.",
                        "GPU Engine Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);

                    AppSettings.SelectedModel = "kokoro-multi-lang-v1_0";
                    AppSettings.Save();
                    _ttsEngine.Dispose();
                    _ttsEngine = new TtsEngine();
                    InitializeTts("cpu");
                    SelectModelInDropdown("kokoro-multi-lang-v1_0");
                }
                finally
                {
                    ProgressBar.IsIndeterminate = false;
                    PythonSidecarEngine.ProgressCallback = null;
                    SetBusy(false, "Ready");
                }
                return;
            }

            if (!ModelDownloader.IsModelReady(newModel))
            {
                var result = MessageBox.Show(
                    $"The {newModel} model is required but not found.\n\nDownload it now?",
                    "Model Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    SetBusy(true, $"Downloading {newModel}...");
                    var progress = new Progress<double>(p =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = p * 100;
                            TxtStatus.Text = $"Downloading {newModel}... {p:P0}";
                        });
                    });

                    try
                    {
                        await ModelDownloader.DownloadModelAsync(newModel, progress);
                        MessageBox.Show("Model downloaded successfully!", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to download model:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        SetBusy(false, "Ready");
                        return;
                    }
                    finally
                    {
                        SetBusy(false, "Ready");
                    }
                }
                else
                {
                    // Revert selection
                    for (int i = 0; i < CmbModel.Items.Count; i++)
                    {
                        if (CmbModel.Items[i] is ComboBoxItem prevItem && prevItem.Tag?.ToString() == AppSettings.SelectedModel)
                        {
                            CmbModel.SelectedIndex = i;
                            break;
                        }
                    }
                    return;
                }
            }

            InitializeTts("cpu");
        }

        #region Undo / Redo
        private void SaveStateForUndo()
        {
            var snapshot = _chapters.Select(c => new ChapterItem
            {
                Title = c.Title,
                Content = c.Content,
                Index = c.Index,
                IsSelected = c.IsSelected
            }).ToList();
            _undoStack.Push(snapshot);
            if (_undoStack.Count > 50) _undoStack.Pop();
            _redoStack.Clear();
        }

        private void CommandBinding_Undo_Executed(object sender, ExecutedRoutedEventArgs e) => MenuUndo_Click(sender, e);
        private void CommandBinding_Redo_Executed(object sender, ExecutedRoutedEventArgs e) => MenuRedo_Click(sender, e);

        private void MenuUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0) return;
            var current = _chapters.Select(c => new ChapterItem { Title = c.Title, Content = c.Content, Index = c.Index, IsSelected = c.IsSelected }).ToList();
            _redoStack.Push(current);
            RestoreState(_undoStack.Pop());
        }

        private void MenuRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_redoStack.Count == 0) return;
            var current = _chapters.Select(c => new ChapterItem { Title = c.Title, Content = c.Content, Index = c.Index, IsSelected = c.IsSelected }).ToList();
            _undoStack.Push(current);
            RestoreState(_redoStack.Pop());
        }

        private void RestoreState(List<ChapterItem> state)
        {
            _chapters = state;
            for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;
            ChaptersList.ItemsSource = null;
            ChaptersList.ItemsSource = _chapters;
            TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
            if (_chapters.Count > 0)
            {
                ChaptersList.SelectedIndex = 0;
            }
            ScheduleDurationRefresh();
        }
        #endregion

        #region File Menu
        private void CommandBinding_New_Executed(object sender, ExecutedRoutedEventArgs e) => MenuNew_Click(sender, e);
        private void CommandBinding_Open_Executed(object sender, ExecutedRoutedEventArgs e) => MenuOpen_Click(sender, e);
        private void CommandBinding_Save_Executed(object sender, ExecutedRoutedEventArgs e) => MenuSave_Click(sender, e);

        private void MenuNew_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo();
            _chapters.Clear();
            _chapters.Add(new ChapterItem { Title = "Chapter 1", Content = "", Index = 0, IsSelected = true });
            TxtFilePath.Text = "Untitled";
            ChaptersList.ItemsSource = null;
            ChaptersList.ItemsSource = _chapters;
            ChaptersList.SelectedIndex = 0;
            TxtChapterCount.Text = "1 chapter";
            ScheduleDurationRefresh();
        }

        private void MenuOpen_Click(object sender, RoutedEventArgs e) => BtnBrowse_Click(sender, e);

        private void MenuSave_Click(object sender, RoutedEventArgs e)
        {
            if (ChaptersList.SelectedItem is ChapterItem chapter)
            {
                chapter.Content = TxtPreview.Text;
            }
            var dlg = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "Project"
            };
            if (dlg.ShowDialog() != true) return;
            var lines = _chapters.Select(c => $"[{c.Title}]\n{c.Content}");
            File.WriteAllText(dlg.FileName, string.Join("\n\n", lines));
            TxtStatus.Text = "Saved";
        }

        private void MenuSaveAs_Click(object sender, RoutedEventArgs e) => MenuSave_Click(sender, e);
        private void MenuImport_Click(object sender, RoutedEventArgs e) => BtnBrowse_Click(sender, e);
        private void MenuExport_Click(object sender, RoutedEventArgs e) => BtnConvert_Click(sender, e);
        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();
        #endregion

        #region Edit Menu
        private void CommandBinding_Find_Executed(object sender, ExecutedRoutedEventArgs e) => MenuFind_Click(sender, e);
        private void CommandBinding_Replace_Executed(object sender, ExecutedRoutedEventArgs e) => MenuReplace_Click(sender, e);
        private void CommandBinding_ReplaceAll_Executed(object sender, ExecutedRoutedEventArgs e) => MenuReplaceAll_Click(sender, e);
        private void CommandBinding_UndoAll_Executed(object sender, ExecutedRoutedEventArgs e) => MenuUndoAll_Click(sender, e);
        private void CommandBinding_RedoAll_Executed(object sender, ExecutedRoutedEventArgs e) => MenuRedoAll_Click(sender, e);

        private void MenuCut_Click(object sender, RoutedEventArgs e) => TxtPreview.Cut();
        private void MenuCopy_Click(object sender, RoutedEventArgs e) => TxtPreview.Copy();
        private void MenuPaste_Click(object sender, RoutedEventArgs e) => TxtPreview.Paste();

        private void MenuFind_Click(object sender, RoutedEventArgs e)
        {
            var search = Microsoft.VisualBasic.Interaction.InputBox("Find text:", "Find", "");
            if (string.IsNullOrWhiteSpace(search)) return;
            var idx = TxtPreview.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                TxtPreview.Focus();
                TxtPreview.Select(idx, search.Length);
            }
            else
            {
                MessageBox.Show("Text not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void MenuReplace_Click(object sender, RoutedEventArgs e) => DoReplace(all: false);
        private void MenuReplaceAll_Click(object sender, RoutedEventArgs e) => DoReplace(all: true);

        private void DoReplace(bool all)
        {
            var find = Microsoft.VisualBasic.Interaction.InputBox("Find text:", all ? "Replace All" : "Replace", "");
            if (string.IsNullOrEmpty(find)) return;
            var repl = Microsoft.VisualBasic.Interaction.InputBox($"Replace \"{find}\" with:", all ? "Replace All" : "Replace", "");

            string text = TxtPreview.Text;
            if (all)
            {
                // Case-insensitive replace-all in the current chapter content box.
                int count = 0;
                string result = Regex.Replace(text, Regex.Escape(find), m => { count++; return repl; }, RegexOptions.IgnoreCase);
                if (count == 0) { MessageBox.Show("Text not found.", "Replace All", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                TxtPreview.Text = result;
                TxtStatus.Text = $"Replaced {count} occurrence(s)";
            }
            else
            {
                int idx = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) { MessageBox.Show("Text not found.", "Replace", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                TxtPreview.Text = text.Remove(idx, find.Length).Insert(idx, repl);
                TxtPreview.Focus();
                TxtPreview.Select(idx, repl.Length);
            }
        }

        private void MenuUndoAll_Click(object sender, RoutedEventArgs e)
        {
            while (_undoStack.Count > 0) MenuUndo_Click(sender, e);
        }

        private void MenuRedoAll_Click(object sender, RoutedEventArgs e)
        {
            while (_redoStack.Count > 0) MenuRedo_Click(sender, e);
        }
        #endregion

        #region View / Theme
        private void MenuTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                string theme = item.Header.ToString()?.TrimStart('_') ?? "Dark";
                AppSettings.Theme = theme;
                ThemeManager.ApplyTheme(theme);
                ApplyMenuThemeCheck(theme);
                AppSettings.Save();
            }
        }

        private void ApplyMenuThemeCheck(string active)
        {
            MenuThemeDark.IsChecked = active == "Dark";
            MenuThemeLight.IsChecked = active == "Light";
            MenuThemeMidnight.IsChecked = active == "Midnight";
        }
        #endregion

        #region Dictionary & Settings Windows
        private void MenuDictionary_Click(object sender, RoutedEventArgs e)
        {
            var dict = new DictionaryWindow { Owner = this };
            dict.ShowDialog();
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow { Owner = this };
            if (settings.ShowDialog() == true)
            {
                ApplyMenuThemeCheck(AppSettings.Theme);
            }
        }
        #endregion

        #region Recent Files
        private void LoadRecentFiles()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TTSApp", "recent.txt");
            if (File.Exists(path))
            {
                _recentFiles.AddRange(File.ReadAllLines(path).Where(File.Exists));
            }
            RefreshRecentFilesMenu();
        }

        private void SaveRecentFiles()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TTSApp");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "recent.txt"), _recentFiles.Take(MaxRecentFiles));
        }

        private void AddRecentFile(string filePath)
        {
            _recentFiles.Remove(filePath);
            _recentFiles.Insert(0, filePath);
            if (_recentFiles.Count > MaxRecentFiles) _recentFiles.RemoveAt(MaxRecentFiles);
            SaveRecentFiles();
            RefreshRecentFilesMenu();
        }

        private void RefreshRecentFilesMenu()
        {
            MenuRecentFiles.Items.Clear();
            if (_recentFiles.Count == 0)
            {
                MenuRecentFiles.IsEnabled = false;
                return;
            }
            MenuRecentFiles.IsEnabled = true;
            foreach (var f in _recentFiles.Take(MaxRecentFiles))
            {
                var item = new MenuItem
                {
                    Header = Path.GetFileName(f),
                    ToolTip = f
                };
                item.Click += (s, e) => LoadFile(f);
                MenuRecentFiles.Items.Add(item);
            }
        }
        #endregion

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Supported files (*.txt;*.pdf;*.epub)|*.txt;*.pdf;*.epub|Text files (*.txt)|*.txt|PDF files (*.pdf)|*.pdf|EPUB files (*.epub)|*.epub|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                LoadFile(dlg.FileName);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length > 0) LoadFile(files[0]);
            }
        }

        private async void LoadFile(string path)
        {
            SaveStateForUndo();
            TxtFilePath.Text = path;
            BtnBrowse.IsEnabled = false;
            CmbModel.IsEnabled = false;
            TxtStatus.Text = $"Loading {Path.GetFileName(path)}...";
            try
            {
                // Parse off the UI thread — big PDFs/EPUBs would otherwise freeze the window.
                _chapters = await Task.Run(() => FileParser.Parse(path));
                ChaptersList.ItemsSource = null;
                ChaptersList.ItemsSource = _chapters;
                TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";

                if (_chapters.Count > 0)
                {
                    ChaptersList.SelectedIndex = 0;
                }
                else
                {
                    TxtChapterTitle.Text = "📝 Chapter Content";
                    TxtWordCount.Text = "No chapters found";
                    TxtDuration.Text = "";
                    TxtPreview.Text = "The file could not be parsed into chapters.";
                }

                ScheduleDurationRefresh();
                TxtStatus.Text = $"Loaded: {Path.GetFileName(path)}";
                AddRecentFile(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ready";
            }
            finally
            {
                BtnBrowse.IsEnabled = true;
                CmbModel.IsEnabled = true;
            }
        }

        private void ChaptersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ChapterItem oldChapter && !_isUpdatingText)
            {
                oldChapter.Content = TxtPreview.Text;
            }

            if (ChaptersList.SelectedItem is ChapterItem chapter)
            {
                _isUpdatingText = true;
                TxtChapterTitle.Text = $"📝 {chapter.Title}";
                if (CmbChapterVoice.Items.Count > 0)
                    CmbChapterVoice.SelectedIndex = Math.Min(chapter.VoiceOverride + 1, CmbChapterVoice.Items.Count - 1);
                int wordCount = chapter.Content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                float speed = (float)SliderSpeed.Value;
                var est = DurationEstimator.Estimate(chapter.Content, speed);
                TxtWordCount.Text = $"{wordCount:N0} words | {chapter.Content.Length:N0} chars";
                TxtDuration.Text = est > TimeSpan.Zero ? $"≈ {DurationEstimator.FormatFriendly(est)}" : "";
                TxtPreview.Text = chapter.Content;
                _isUpdatingText = false;
            }
            ScheduleDurationRefresh();
        }

        private void TxtPreview_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingText) return;
            if (ChaptersList.SelectedItem is ChapterItem chapter)
            {
                chapter.Content = TxtPreview.Text;
                int wordCount = chapter.Content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                float speed = (float)SliderSpeed.Value;
                var est = DurationEstimator.Estimate(chapter.Content, speed);
                TxtWordCount.Text = $"{wordCount:N0} words | {chapter.Content.Length:N0} chars";
                TxtDuration.Text = est > TimeSpan.Zero ? $"≈ {DurationEstimator.FormatFriendly(est)}" : "";
            }
            ScheduleDurationRefresh();
        }

        private void BtnAddChapter_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo();
            int newIndex = _chapters.Count;
            var chapter = new ChapterItem
            {
                Title = $"New Chapter {newIndex + 1}",
                Content = "",
                Index = newIndex,
                IsSelected = true
            };
            _chapters.Add(chapter);
            ChaptersList.Items.Refresh();
            ChaptersList.SelectedItem = chapter;
            ChaptersList.ScrollIntoView(chapter);
            TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
            ScheduleDurationRefresh();
        }

        private void BtnDeleteChapter_Click(object sender, RoutedEventArgs e)
        {
            if (ChaptersList.SelectedItem is ChapterItem chapter)
            {
                var result = MessageBox.Show($"Delete '{chapter.Title}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    SaveStateForUndo();
                    _chapters.Remove(chapter);
                    for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;
                    ChaptersList.Items.Refresh();
                    if (_chapters.Count > 0)
                    {
                        ChaptersList.SelectedIndex = Math.Min(chapter.Index, _chapters.Count - 1);
                    }
                    else
                    {
                        _isUpdatingText = true;
                        TxtPreview.Text = "";
                        TxtChapterTitle.Text = "📝 Chapter Content";
                        TxtWordCount.Text = "No chapters";
                        TxtDuration.Text = "";
                        _isUpdatingText = false;
                    }
                    TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
                    ScheduleDurationRefresh();
                }
            }
        }

        // #9 — filter the chapter list by title.
        private void TxtChapterSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ChaptersList.ItemsSource == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ChaptersList.ItemsSource);
            if (view == null) return;
            string q = TxtChapterSearch.Text.Trim();
            view.Filter = string.IsNullOrEmpty(q)
                ? null
                : obj => obj is ChapterItem c && c.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
            view.Refresh();
        }

        // #8 — reorder chapters.
        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedChapter(-1);
        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedChapter(1);

        private void MoveSelectedChapter(int delta)
        {
            if (ChaptersList.SelectedItem is not ChapterItem ch) return;
            int idx = _chapters.IndexOf(ch);
            int target = idx + delta;
            if (idx < 0 || target < 0 || target >= _chapters.Count) return;

            SaveStateForUndo();
            _chapters.RemoveAt(idx);
            _chapters.Insert(target, ch);
            for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;
            ChaptersList.Items.Refresh();
            ChaptersList.SelectedItem = ch;
            ChaptersList.ScrollIntoView(ch);
            ScheduleDurationRefresh();
        }

        #region Audio Player
        private void StartPlayback(string filePath)
        {
            StopPlayback();
            _currentPreviewFile = filePath;
            _waveReader = new WaveFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveReader);
            _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;

            SliderSeek.Maximum = _waveReader.TotalTime.TotalSeconds;
            SliderSeek.Value = 0;
            BtnPlayPause.Content = "⏸";

            _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _playbackTimer.Tick += PlaybackTimer_Tick;
            _playbackTimer.Start();

            DrawWaveform(filePath);
            _waveOut.Play();
        }

        private void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            if (_waveReader != null && _waveOut != null)
            {
                SliderSeek.Value = _waveReader.CurrentTime.TotalSeconds;
                LblTime.Text = $"{_waveReader.CurrentTime.Minutes:D2}:{_waveReader.CurrentTime.Seconds:D2} / {_waveReader.TotalTime.Minutes:D2}:{_waveReader.TotalTime.Seconds:D2}";
                string title = ChaptersList.SelectedItem is ChapterItem ch ? ch.Title : "Preview";
                SyncMiniPlayerState(_waveOut.PlaybackState == PlaybackState.Playing, title, _waveReader.CurrentTime, _waveReader.TotalTime);
            }
        }

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                BtnPlayPause.Content = "▶";
                _playbackTimer?.Stop();
                if (_waveReader != null)
                {
                    SliderSeek.Value = _waveReader.CurrentTime.TotalSeconds;
                    LblTime.Text = $"{_waveReader.CurrentTime.Minutes:D2}:{_waveReader.CurrentTime.Seconds:D2} / {_waveReader.TotalTime.Minutes:D2}:{_waveReader.TotalTime.Seconds:D2}";
                }
            });
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_waveOut == null) return;
            if (_waveOut.PlaybackState == PlaybackState.Playing)
            {
                _waveOut.Pause();
                BtnPlayPause.Content = "▶";
            }
            else
            {
                _waveOut.Play();
                BtnPlayPause.Content = "⏸";
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void StopPlayback()
        {
            _playbackTimer?.Stop();
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveReader?.Dispose();
            _waveOut = null;
            _waveReader = null;
            BtnPlayPause.Content = "▶";
            SliderSeek.Value = 0;
            LblTime.Text = "0:00 / 0:00";
            if (_currentPreviewFile != null)
            {
                try { File.Delete(_currentPreviewFile); } catch { }
                _currentPreviewFile = null;
            }
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            if (_waveReader == null) return;
            var newTime = _waveReader.CurrentTime - TimeSpan.FromSeconds(AppSettings.RewindSeconds);
            _waveReader.CurrentTime = newTime < TimeSpan.Zero ? TimeSpan.Zero : newTime;
            SliderSeek.Value = _waveReader.CurrentTime.TotalSeconds;
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (_waveReader == null) return;
            var newTime = _waveReader.CurrentTime + TimeSpan.FromSeconds(AppSettings.ForwardSeconds);
            _waveReader.CurrentTime = newTime > _waveReader.TotalTime ? _waveReader.TotalTime : newTime;
            SliderSeek.Value = _waveReader.CurrentTime.TotalSeconds;
        }

        private void SliderSeek_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_waveReader != null)
            {
                _waveReader.CurrentTime = TimeSpan.FromSeconds(SliderSeek.Value);
            }
        }

        internal void MiniRewind() => BtnRewind_Click(this, new RoutedEventArgs());
        internal void MiniForward() => BtnForward_Click(this, new RoutedEventArgs());
        internal void MiniStop() => BtnStop_Click(this, new RoutedEventArgs());
        internal void MiniPlayPause() => BtnPlayPause_Click(this, new RoutedEventArgs());
        internal void MiniSeek(double seconds)
        {
            if (_waveReader != null)
            {
                _waveReader.CurrentTime = TimeSpan.FromSeconds(seconds);
                SliderSeek.Value = seconds;
            }
        }
        #endregion

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (!_ttsEngine.IsInitialized)
            {
                MessageBox.Show("TTS engine is not initialized. Please wait for the model to load.", "Not Ready", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string textToPreview = TxtPreview.SelectedText;
            if (string.IsNullOrWhiteSpace(textToPreview))
            {
                textToPreview = TxtPreview.Text.Length > 200 ? TxtPreview.Text.Substring(0, 200) + "..." : TxtPreview.Text;
            }

            if (string.IsNullOrWhiteSpace(textToPreview))
            {
                MessageBox.Show("No text to preview. Select a chapter and highlight some text, or type content first.", "Empty", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            textToPreview = AppSettings.ApplyDictionary(textToPreview);

            int speakerId = CmbVoice.SelectedIndex;
            float speed = (float)SliderSpeed.Value;
            string tempFile = Path.Combine(Path.GetTempPath(), $"tts_preview_{Guid.NewGuid()}.wav");

            BtnPreview.IsEnabled = false;
            BtnConvert.IsEnabled = false;
            TxtStatus.Text = "Generating preview...";

            await Task.Run(() =>
            {
                try
                {
                    _ttsEngine.Generate(textToPreview, speakerId, speed, tempFile);
                    Dispatcher.Invoke(() =>
                    {
                        TxtStatus.Text = "Playing preview...";
                        StartPlayback(tempFile);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Preview failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        BtnPreview.IsEnabled = true;
                        BtnConvert.IsEnabled = true;
                        TxtStatus.Text = "Ready";
                    });
                }
            });
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _chapters) c.IsSelected = true;
            ScheduleDurationRefresh();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _chapters) c.IsSelected = false;
            ScheduleDurationRefresh();
        }

        private void ChkChapter_IsCheckedChanged(object sender, RoutedEventArgs e)
        {
            ScheduleDurationRefresh();
        }

        // Coalesce rapid triggers (keystrokes, checkbox spam) into a single recalc.
        private void ScheduleDurationRefresh()
        {
            if (_durationDebounce == null)
            {
                _durationDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _durationDebounce.Tick += (_, _) =>
                {
                    _durationDebounce!.Stop();
                    RefreshTotalDurationNow();
                };
            }
            _durationDebounce.Stop();
            _durationDebounce.Start();
        }

        private void RefreshTotalDurationNow()
        {
            float speed = (float)SliderSpeed.Value;
            var selected = _chapters.Where(c => c.IsSelected).ToList();
            TimeSpan total = TimeSpan.Zero;
            foreach (var ch in selected)
                total += DurationEstimator.Estimate(ch.Content, speed);

            Dispatcher.Invoke(() =>
            {
                if (selected.Count == 0)
                    TxtTotalDuration.Text = "";
                else
                    TxtTotalDuration.Text = total > TimeSpan.Zero ? $"≈ {DurationEstimator.FormatFriendly(total)} total" : "";
            });
        }

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            // Already running → this click is a cancel request.
            if (_convertCts != null)
            {
                _convertCts.Cancel();
                BtnConvert.IsEnabled = false;
                TxtStatus.Text = "Cancelling after current chapter...";
                return;
            }

            var selected = _chapters.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one chapter to convert.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // #7 — offer to skip chapters already converted this session (resume).
            var alreadyDone = selected.Where(c => c.Status == ConvertState.Done).ToList();
            if (alreadyDone.Count > 0)
            {
                var r = MessageBox.Show(
                    $"{alreadyDone.Count} of the selected chapters are already converted.\n\nSkip them and convert only the rest?",
                    "Resume", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    selected = selected.Where(c => c.Status != ConvertState.Done).ToList();
                    if (selected.Count == 0)
                    {
                        MessageBox.Show("Nothing left to convert.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
            }

            bool merge = ChkMerge.IsChecked == true;
            bool isM4B = CmbFormat.SelectedIndex == 2;
            if (isM4B) merge = true; // M4B is always a merged format

            var provider = CmbProvider.SelectedIndex switch
            {
                1 => "cuda",
                2 => "dml",
                _ => "cpu"
            };

            // The CPU/CUDA/DirectML selector only applies to in-process Kokoro; the sidecar manages its own device.
            if (!IsSidecarModel(AppSettings.SelectedModel) &&
                (!_ttsEngine.IsInitialized || _ttsEngine.CurrentProvider != provider))
            {
                try
                {
                    _ttsEngine.Dispose();
                    _ttsEngine = CreateEngine(AppSettings.SelectedModel);
                    InitializeTts(provider);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to switch provider:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string filter;
            if (isM4B)
                filter = "M4B Audiobook|*.m4b";
            else if (merge)
                filter = "MP3 Audio|*.mp3|WAV Audio|*.wav";
            else
                filter = CmbFormat.SelectedIndex == 0 ? "WAV Audio|*.wav" : "MP3 Audio|*.mp3";

            var dlg = new SaveFileDialog { Filter = filter, FileName = "Audiobook" };
            if (!string.IsNullOrEmpty(AppSettings.LastOutputDir) && Directory.Exists(AppSettings.LastOutputDir))
                dlg.InitialDirectory = AppSettings.LastOutputDir;
            if (dlg.ShowDialog() != true) return;

            AppSettings.LastOutputDir = Path.GetDirectoryName(dlg.FileName);
            AppSettings.Save();

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            string basePath = dlg.FileName;
            if (isM4B)
            {
                if (ext != ".m4b")
                {
                    ext = ".m4b";
                    basePath = Path.ChangeExtension(basePath, ".m4b");
                }
            }
            else if (ext != ".wav" && ext != ".mp3")
            {
                ext = ".mp3";
                basePath += ext;
            }

            _convertCts = new CancellationTokenSource();
            var token = _convertCts.Token;

            SetBusy(true, "Converting...");
            BtnBrowse.IsEnabled = false;
            BtnConvert.IsEnabled = true;        // stays clickable as a Cancel button
            LblConvert.Text = "Cancel";
            ProgressBar.Maximum = selected.Count;
            ProgressBar.Value = 0;
            foreach (var ch in selected) ch.Status = ConvertState.Queued;

            int speakerId = CmbVoice.SelectedIndex;
            float speed = (float)SliderSpeed.Value;
            bool announce = AppSettings.AnnounceChapterTitle;
            var chapterWavs = new List<string>();
            var chapterInfos = new List<(string Title, TimeSpan Start)>();
            TimeSpan currentStart = TimeSpan.Zero;

            try
            {
            await Task.Run(() =>
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var chapter = selected[i];
                    Dispatcher.Invoke(() => chapter.Status = ConvertState.Converting);
                    string text = chapter.Content;
                    text = AppSettings.ApplyDictionary(text);

                    if (announce)
                    {
                        string announceText = $"Chapter {chapter.Index + 1}. {chapter.Title}.";
                        if (!ContentStartsWithChapterHeading(text, chapter.Title))
                        {
                            text = $"{announceText} {text}";
                        }
                    }

                    string outputFile;
                    if (merge)
                    {
                        outputFile = Path.Combine(Path.GetTempPath(), $"tts_merge_ch{chapter.Index}_{Guid.NewGuid()}.wav");
                        chapterWavs.Add(outputFile);
                    }
                    else
                    {
                        outputFile = selected.Count == 1
                            ? basePath
                            : Path.Combine(Path.GetDirectoryName(basePath)!, $"{Path.GetFileNameWithoutExtension(basePath)}_ch{chapter.Index + 1}{ext}");
                    }

                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _isUpdatingText = true;
                            TxtStatus.Text = $"Converting {i + 1} of {selected.Count}: {chapter.Title}";
                            TxtChapterTitle.Text = $"🔊 {chapter.Title}";
                            TxtPreview.Text = text.Length > 4000 ? text.Substring(0, 4000) + "\n\n..." : text;
                            ProgressBar.Value = i;
                            ChaptersList.SelectedItem = chapter;
                            ChaptersList.ScrollIntoView(chapter);
                            _isUpdatingText = false;
                        });

                        // #19 — use the chapter's voice override if set, else the global voice.
                        int chapterVoice = chapter.VoiceOverride >= 0 ? chapter.VoiceOverride : speakerId;
                        _ttsEngine.Generate(text, chapterVoice, speed, outputFile);

                        // Per-chapter: trim always; normalize per-chapter only when NOT merging
                        // (merged output is normalized once as a whole below).
                        ProcessAudio(outputFile, normalizeNow: !merge);

                        if (merge)
                        {
                            chapterInfos.Add((chapter.Title, currentStart));
                            using var r = new WaveFileReader(outputFile);
                            currentStart += r.TotalTime;
                        }
                        else if (ext == ".mp3" && outputFile.EndsWith(".mp3"))
                        {
                            EmbedMp3Metadata(outputFile, chapter.Title);
                        }

                        Dispatcher.Invoke(() => chapter.Status = ConvertState.Done);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            chapter.Status = ConvertState.Failed;
                            MessageBox.Show($"Error converting '{chapter.Title}':\n{ex.Message}", "Conversion Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }

                // Skip merging/export if the user cancelled.
                if (token.IsCancellationRequested) return;

                // Merge chapters into single file
                if (merge && chapterWavs.Count > 0)
                {
                    Dispatcher.Invoke(() => TxtStatus.Text = "Merging chapters...");
                    string mergedWav = Path.Combine(Path.GetTempPath(), $"tts_merged_{Guid.NewGuid()}.wav");
                    MergeWavFiles(chapterWavs, mergedWav);

                    // Normalize the whole book once after merge (consistent loudness end-to-end).
                    if (AppSettings.NormalizationMode != 0)
                        ApplyNormalization(mergedWav);

                    // Optional background bed + intro/outro (no-op unless configured + ffmpeg present).
                    string mixed = AudioMixer.Apply(mergedWav);
                    if (mixed != mergedWav)
                    {
                        try { File.Delete(mergedWav); } catch { }
                        mergedWav = mixed;
                    }

                    if (isM4B)
                    {
                        Dispatcher.Invoke(() => TxtStatus.Text = "Creating M4B audiobook...");
                        string m4bPath = basePath;
                        bool ok = M4BHelper.CreateM4B(mergedWav, m4bPath, chapterInfos, _coverImagePath);
                        if (!ok)
                        {
                            string mp3Path = Path.ChangeExtension(basePath, ".mp3");
                            ConvertWavToMp3(mergedWav, mp3Path);
                            EmbedMp3MetadataWithChapters(mp3Path, chapterInfos);
                            Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show("ffmpeg not found or M4B creation failed. Saved as MP3 instead.", "M4B Export", MessageBoxButton.OK, MessageBoxImage.Information);
                            });
                        }
                        File.Delete(mergedWav);
                    }
                    else if (ext == ".mp3")
                    {
                        string mergedMp3 = basePath;
                        // Prefer ffmpeg so the MP3 gets real chapter marks; fall back to Lame (no chapters).
                        if (!M4BHelper.CreateMp3WithChapters(mergedWav, mergedMp3, chapterInfos, _coverImagePath))
                        {
                            ConvertWavToMp3(mergedWav, mergedMp3);
                            EmbedMp3MetadataWithChapters(mergedMp3, chapterInfos);
                        }
                        File.Delete(mergedWav);
                    }
                    else
                    {
                        File.Move(mergedWav, basePath, true);
                    }

                    foreach (var w in chapterWavs)
                    {
                        try { File.Delete(w); } catch { }
                    }
                }
            });

                if (token.IsCancellationRequested)
                {
                    TxtStatus.Text = "Conversion cancelled.";
                    MessageBox.Show("Conversion cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    ProgressBar.Value = selected.Count;
                    TxtStatus.Text = "Conversion complete!";
                    var open = MessageBox.Show("Audio conversion finished!\n\nOpen the output folder?",
                        "Done", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (open == MessageBoxResult.Yes)
                    {
                        try
                        {
                            // Select the produced file in Explorer.
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{basePath}\"");
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Conversion failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _convertCts?.Dispose();
                _convertCts = null;
                LblConvert.Text = "Convert";
                SetBusy(false, "Ready");
                BtnBrowse.IsEnabled = true;
                BtnConvert.IsEnabled = true;
            }
        }

        private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblSpeed != null)
                LblSpeed.Text = $"{e.NewValue:F1}x";
            ScheduleDurationRefresh();
            // Also refresh current chapter duration display
            if (ChaptersList.SelectedItem is ChapterItem chapter && !_isUpdatingText)
            {
                float speed = (float)e.NewValue;
                var est = DurationEstimator.Estimate(chapter.Content, speed);
                TxtDuration.Text = est > TimeSpan.Zero ? $"≈ {DurationEstimator.FormatFriendly(est)}" : "";
            }
        }

        // #15/#16 — per-output audio processing: trim silence, then normalize per the chosen mode.
        private static void ProcessAudio(string wavPath, bool normalizeNow)
        {
            if (!wavPath.EndsWith(".wav")) return;
            if (AppSettings.TrimSilence) AudioNormalizer.TrimSilence(wavPath);
            if (normalizeNow && AppSettings.NormalizationMode != 0) ApplyNormalization(wavPath);
        }

        private static void ApplyNormalization(string wavPath)
        {
            switch (AppSettings.NormalizationMode)
            {
                case 1: AudioNormalizer.NormalizePeak(wavPath); break;
                case 2: AudioNormalizer.NormalizeLoudness(wavPath); break;
                case 3: AudioNormalizer.NormalizeLufs(wavPath, (float)AppSettings.TargetLufs); break;
            }
        }

        private void SetBusy(bool busy, string status)
        {
            TxtStatus.Text = status;
            if (!busy) ProgressBar.Value = 0;
            // Block actions that need a ready engine while a load/convert is in progress.
            BtnPreview.IsEnabled = !busy;
            BtnConvert.IsEnabled = !busy;
            CmbModel.IsEnabled = !busy;
        }

        #region Audio Export Helpers
        private static void MergeWavFiles(List<string> inputFiles, string outputPath)
        {
            using var first = new WaveFileReader(inputFiles[0]);
            using var writer = new WaveFileWriter(outputPath, first.WaveFormat);
            foreach (var file in inputFiles)
            {
                using var reader = new WaveFileReader(file);
                if (reader.WaveFormat.Encoding != first.WaveFormat.Encoding ||
                    reader.WaveFormat.SampleRate != first.WaveFormat.SampleRate ||
                    reader.WaveFormat.Channels != first.WaveFormat.Channels)
                    continue;
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    writer.Write(buffer, 0, bytesRead);
            }
        }

        private static void ConvertWavToMp3(string wavPath, string mp3Path)
        {
            using var reader = new WaveFileReader(wavPath);
            using var writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, 192);
            reader.CopyTo(writer);
        }

        private void EmbedMp3Metadata(string mp3Path, string chapterTitle)
        {
            try
            {
                var file = TagLib.File.Create(mp3Path);
                file.Tag.Title = chapterTitle;
                file.Tag.Album = "AI Audiobook";
                EmbedCoverArt(file);
                file.Save();
            }
            catch { /* ignore tag errors */ }
        }

        private void EmbedMp3MetadataWithChapters(string mp3Path, List<(string Title, TimeSpan Start)> chapters)
        {
            try
            {
                var file = TagLib.File.Create(mp3Path);
                file.Tag.Title = "Audiobook";
                file.Tag.Album = "AI Audiobook";
                EmbedCoverArt(file);
                // TagLib# does not support ID3v2 CHAP frames natively, but we can set basic tags
                file.Save();
            }
            catch { /* ignore tag errors */ }
        }

        private void EmbedCoverArt(TagLib.File file)
        {
            if (!string.IsNullOrEmpty(_coverImagePath) && File.Exists(_coverImagePath))
            {
                try
                {
                    var pic = new TagLib.Picture(_coverImagePath);
                    file.Tag.Pictures = new[] { pic };
                }
                catch { }
            }
        }


        #endregion

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _autoSaveTimer?.Stop();
            SaveAutoSave();
            _miniPlayer?.Close();
            StopPlayback();
            base.OnClosing(e);
        }

        #region Auto-Save
        private void StartAutoSaveTimer()
        {
            _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _autoSaveTimer.Tick += (s, e) => SaveAutoSave();
            _autoSaveTimer.Start();
        }

        private void SaveAutoSave()
        {
            try
            {
                if (_chapters.Count > 0)
                {
                    ProjectFile.Save(AppSettings.GetAutoSavePath(), _chapters,
                        CmbVoice.SelectedIndex, (float)SliderSpeed.Value,
                        CmbFormat.SelectedIndex, CmbProvider.SelectedIndex, _coverImagePath);
                }
            }
            catch { /* ignore */ }
        }

        private void TryRestoreAutoSave()
        {
            var path = AppSettings.GetAutoSavePath();
            if (!File.Exists(path)) return;

            try
            {
                var autoSave = ProjectFile.Load(path);
                if (autoSave != null && autoSave.Chapters.Count > 0)
                {
                    var result = MessageBox.Show(
                        "An unsaved auto-recovery draft was found. Would you like to restore it?",
                        "Restore Draft", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _chapters = autoSave.Chapters.Select(c => new ChapterItem
                        {
                            Title = c.Title,
                            Content = c.Content,
                            IsSelected = c.IsSelected
                        }).ToList();
                        for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;
                        ChaptersList.ItemsSource = null;
                        ChaptersList.ItemsSource = _chapters;
                        TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
                        if (_chapters.Count > 0) ChaptersList.SelectedIndex = 0;

                        CmbVoice.SelectedIndex = Math.Min(autoSave.SelectedVoiceIndex, CmbVoice.Items.Count - 1);
                        SliderSpeed.Value = autoSave.Speed;
                        CmbFormat.SelectedIndex = autoSave.FormatIndex;
                        CmbProvider.SelectedIndex = autoSave.ProviderIndex;
                        _coverImagePath = autoSave.CoverImagePath;
                        LoadCoverImage();
                        TxtFilePath.Text = "Restored draft";
                        ScheduleDurationRefresh();
                    }
                }
            }
            catch { /* ignore */ }
        }
        #endregion

        #region Project Files
        private void MenuSaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (ChaptersList.SelectedItem is ChapterItem chapter)
                chapter.Content = TxtPreview.Text;

            var dlg = new SaveFileDialog
            {
                Filter = "TTSApp Project (*.ttsp)|*.ttsp",
                FileName = _currentProjectPath != null ? Path.GetFileNameWithoutExtension(_currentProjectPath) : "Project"
            };
            if (dlg.ShowDialog() != true) return;

            ProjectFile.Save(dlg.FileName, _chapters,
                CmbVoice.SelectedIndex, (float)SliderSpeed.Value,
                CmbFormat.SelectedIndex, CmbProvider.SelectedIndex, _coverImagePath);

            _currentProjectPath = dlg.FileName;
            AppSettings.LastProjectPath = _currentProjectPath;
            AppSettings.Save();
            TxtStatus.Text = "Project saved";
        }

        private void MenuOpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "TTSApp Project (*.ttsp)|*.ttsp"
            };
            if (dlg.ShowDialog() != true) return;
            LoadProject(dlg.FileName);
        }

        private void LoadProject(string path)
        {
            try
            {
                var project = ProjectFile.Load(path);
                if (project == null) throw new Exception("Failed to load project file.");

                _chapters = project.Chapters.Select(c => new ChapterItem
                {
                    Title = c.Title,
                    Content = c.Content,
                    IsSelected = c.IsSelected
                }).ToList();
                for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;

                ChaptersList.ItemsSource = null;
                ChaptersList.ItemsSource = _chapters;
                TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
                if (_chapters.Count > 0) ChaptersList.SelectedIndex = 0;

                CmbVoice.SelectedIndex = Math.Min(project.SelectedVoiceIndex, CmbVoice.Items.Count - 1);
                SliderSpeed.Value = project.Speed;
                CmbFormat.SelectedIndex = project.FormatIndex;
                CmbProvider.SelectedIndex = project.ProviderIndex;
                _coverImagePath = project.CoverImagePath;
                LoadCoverImage();

                _currentProjectPath = path;
                AppSettings.LastProjectPath = path;
                AppSettings.Save();
                TxtFilePath.Text = Path.GetFileName(path);
                TxtStatus.Text = "Project loaded";
                ScheduleDurationRefresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Cover Art
        private void ImgCover_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _coverImagePath = dlg.FileName;
                LoadCoverImage();
            }
        }

        private void LoadCoverImage()
        {
            if (!string.IsNullOrEmpty(_coverImagePath) && File.Exists(_coverImagePath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_coverImagePath);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ImgCover.Source = bmp;
                    TxtCoverHint.Visibility = Visibility.Collapsed;
                }
                catch { /* ignore bad image */ }
            }
            else
            {
                ImgCover.Source = null;
                TxtCoverHint.Visibility = Visibility.Visible;
            }
        }
        #endregion

        #region Mini Player
        private void MenuMiniPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_miniPlayer == null || !_miniPlayer.IsVisible)
            {
                _miniPlayer = new MiniPlayerWindow(this);
                _miniPlayer.Show();
            }
            else
            {
                _miniPlayer.Activate();
            }
        }

        internal void SyncMiniPlayerState(bool isPlaying, string title, TimeSpan current, TimeSpan total)
        {
            _miniPlayer?.UpdateState(isPlaying, title, current, total);
        }
        #endregion

        #region Waveform
        private void DrawWaveform(string wavPath)
        {
            WaveformCanvas.Children.Clear();
            if (!File.Exists(wavPath)) return;

            try
            {
                using var reader = new WaveFileReader(wavPath);
                int sampleRate = reader.WaveFormat.SampleRate;
                int channels = reader.WaveFormat.Channels;
                int bytesPerSample = reader.WaveFormat.BitsPerSample / 8;
                long totalSamples = reader.Length / (channels * bytesPerSample);
                int width = (int)WaveformCanvas.ActualWidth;
                if (width <= 0) width = 400;
                int height = (int)WaveformCanvas.ActualHeight;
                if (height <= 0) height = 40;

                int samplesPerPixel = Math.Max(1, (int)(totalSamples / width));
                var buffer = new byte[reader.WaveFormat.BlockAlign * samplesPerPixel];
                var polyline = new System.Windows.Shapes.Polyline
                {
                    Stroke = System.Windows.Media.Brushes.Gray,
                    StrokeThickness = 1,
                    Opacity = 0.6
                };

                double midY = height / 2.0;
                for (int x = 0; x < width; x++)
                {
                    int bytesRead = reader.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    float peak = 0f;
                    if (bytesPerSample == 2)
                    {
                        for (int i = 0; i < bytesRead; i += 2 * channels)
                        {
                            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                            peak = Math.Max(peak, Math.Abs(s / 32768f));
                        }
                    }
                    else if (bytesPerSample == 4 && reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                    {
                        for (int i = 0; i < bytesRead; i += 4 * channels)
                        {
                            float s = BitConverter.ToSingle(buffer, i);
                            peak = Math.Max(peak, Math.Abs(s));
                        }
                    }

                    double y = midY - (peak * midY);
                    polyline.Points.Add(new System.Windows.Point(x, y));
                }

                WaveformCanvas.Children.Add(polyline);

                // Mirror bottom half
                var bottomPoly = new System.Windows.Shapes.Polyline
                {
                    Stroke = System.Windows.Media.Brushes.Gray,
                    StrokeThickness = 1,
                    Opacity = 0.6
                };
                foreach (var pt in polyline.Points)
                {
                    bottomPoly.Points.Add(new System.Windows.Point(pt.X, midY + (midY - pt.Y)));
                }
                WaveformCanvas.Children.Add(bottomPoly);
            }
            catch { /* ignore waveform errors */ }
        }
        #endregion

        #region URL Import
        private async void MenuImportUrl_Click(object sender, RoutedEventArgs e)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("Enter URL to a web novel chapter:", "Import from URL", "");
            if (string.IsNullOrWhiteSpace(input)) return;
            await ImportUrlAsChapterAsync(input);
        }

        private async System.Threading.Tasks.Task<bool> ImportUrlAsChapterAsync(string input)
        {
            SetBusy(true, "Fetching...");
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var html = await client.GetStringAsync(input);

                // Find the "next chapter" link from the raw HTML before we strip navigation.
                string? nextUrl = ExtractNextLink(html, input);

                // Strip script/style/nav tags
                html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<nav[^>]*>.*?</nav>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<header[^>]*>.*?</header>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<footer[^>]*>.*?</footer>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                // Try to extract chapter content from common containers
                string content = "";
                var contentMatch = Regex.Match(html, @"<div[^>]*class=[""'][^""']*chapter-content[^""']*[""'][^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (contentMatch.Success) content = contentMatch.Groups[1].Value;
                else
                {
                    contentMatch = Regex.Match(html, @"<div[^>]*class=[""'][^""']*entry-content[^""']*[""'][^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (contentMatch.Success) content = contentMatch.Groups[1].Value;
                    else
                    {
                        contentMatch = Regex.Match(html, @"<article[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (contentMatch.Success) content = contentMatch.Groups[1].Value;
                    }
                }

                if (string.IsNullOrWhiteSpace(content))
                    content = html; // fallback to full page

                // Convert HTML to plain text
                content = content.Replace("</p>", "\n\n").Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
                content = Regex.Replace(content, @"<[^>]+>", "");
                content = System.Net.WebUtility.HtmlDecode(content);
                content = Regex.Replace(content, @"[ \t]+", " ");
                content = Regex.Replace(content, @"\n\s*\n", "\n\n");
                content = content.Trim();

                // Remove web junk: echoed book/site header lines + repeated "Chapter N" tokens.
                content = CleanImportedContent(content);

                // Prefer the chapter heading found in the content; fall back to the page <title>.
                string title = ExtractChapterTitleFromContent(content) ?? "Imported Chapter";
                if (title == "Imported Chapter")
                {
                    var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success)
                        title = CleanWebTitle(System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value));
                }

                SaveStateForUndo();
                // Append as a new chapter (keep any existing book).
                var newChapter = new ChapterItem
                {
                    Title = title, Content = content, Index = _chapters.Count, IsSelected = true,
                    SourceUrl = input, NextUrl = nextUrl
                };
                _chapters.Add(newChapter);
                ChaptersList.ItemsSource = null;
                ChaptersList.ItemsSource = _chapters;
                ChaptersList.SelectedItem = newChapter;
                ChaptersList.ScrollIntoView(newChapter);
                TxtChapterCount.Text = $"{_chapters.Count} chapter(s)";
                TxtStatus.Text = $"Imported from URL: {title}";
                ScheduleDurationRefresh();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to fetch URL:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                SetBusy(false, "Ready");
            }
        }

        // Clean a page <title> into just the chapter: drop the book-name prefix before "Chapter N",
        // and strip a trailing site name after a separator (- – — |).
        private static string CleanWebTitle(string raw)
        {
            string t = raw.Trim();

            // Drop everything before "Chapter N" (removes the book name prefix).
            var ch = Regex.Match(t, @"(chapter\s+\d+.*)$", RegexOptions.IgnoreCase);
            if (ch.Success) t = ch.Groups[1].Value.Trim();

            // Strip a trailing site name: remove the last "<sep> <segment>" when a separator is present.
            // (Keeps "Chapter 1: Crimson" but trims "Chapter 1: Crimson – SiteName".)
            if (Regex.IsMatch(t, @"[|\-–—]"))
                t = Regex.Replace(t, @"\s*[|\-–—]\s*[^|\-–—]*$", "").Trim();

            return string.IsNullOrWhiteSpace(t) ? raw.Trim() : t;
        }

        // Collapse repeated "Chapter N" tokens: "Chapter 1 : Chapter 1 Chapter 1. A Third" -> "Chapter 1. A Third".
        private static string CollapseChapterRepeats(string s)
        {
            return Regex.Replace(s, @"(?:Chapter\s+\d+\s*[:.\-–—]?\s*){2,}", m =>
            {
                var num = Regex.Match(m.Value, @"\d+").Value;
                return $"Chapter {num}. ";
            }, RegexOptions.IgnoreCase);
        }

        // Strip echoed book/site header lines and collapse repeated chapter labels from imported web text.
        private static string CleanImportedContent(string content)
        {
            content = CollapseChapterRepeats(content);
            var lines = content.Replace("\r\n", "\n").Split('\n').ToList();

            int i = 0;
            while (i < lines.Count)
            {
                var t = lines[i].Trim();
                if (t.Length == 0) { lines.RemoveAt(i); continue; }
                // A "Book - Chapter N - Site" header line has 2+ dash/pipe separators → drop it.
                if (Regex.Matches(t, @"\s[-–—|]\s").Count >= 2) { lines.RemoveAt(i); continue; }
                break; // first real line reached
            }
            return string.Join("\n", lines).Trim();
        }

        // Pull the chapter title from a heading line inside the content (e.g. "Chapter 1. A Third-Rate Warrior...").
        private static string? ExtractChapterTitleFromContent(string content)
        {
            var m = Regex.Match(content, @"^.*?chapter\s+\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var line = m.Value.Trim();
            // Drop a leading book-name prefix before "Chapter N".
            var ch = Regex.Match(line, @"(chapter\s+\d+.*)$", RegexOptions.IgnoreCase);
            if (ch.Success) line = ch.Groups[1].Value.Trim();
            if (line.Length > 80) line = line.Substring(0, 80).TrimEnd() + "…";
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }

        // Find a "next chapter" link: prefer rel="next", else an anchor whose text contains "next".
        private static string? ExtractNextLink(string html, string baseUrl)
        {
            string? href = null;
            var rel = Regex.Match(html, @"<a\b[^>]*\brel=[""']next[""'][^>]*\bhref=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!rel.Success)
                rel = Regex.Match(html, @"<a\b[^>]*\bhref=[""']([^""']+)[""'][^>]*\brel=[""']next[""']", RegexOptions.IgnoreCase);
            if (rel.Success) href = rel.Groups[1].Value;

            if (href == null)
            {
                // Anchor whose visible text starts with / contains "next" (e.g. "Next Chapter", "Next ›").
                var m = Regex.Match(html, @"<a\b[^>]*\bhref=[""']([^""']+)[""'][^>]*>\s*(?:<[^>]+>\s*)*[^<]*next[^<]*<",
                    RegexOptions.IgnoreCase);
                if (m.Success) href = m.Groups[1].Value;
            }

            if (!string.IsNullOrWhiteSpace(href) && !href.StartsWith("#") && !href.StartsWith("javascript:"))
            {
                try { return new Uri(new Uri(baseUrl), href).ToString(); }
                catch { /* fall through to URL-increment */ }
            }

            // Fallback: many novel sites put the chapter number at the end of the URL
            // (…/series/<slug>/1 → …/2). Increment the last number in the path.
            var num = Regex.Match(baseUrl, @"^(.*?/)(\d+)(/?)([?#].*)?$");
            if (num.Success && int.TryParse(num.Groups[2].Value, out int n))
                return $"{num.Groups[1].Value}{n + 1}{num.Groups[3].Value}{num.Groups[4].Value}";

            return null;
        }
        #endregion

        private static bool ContentStartsWithChapterHeading(string text, string chapterTitle)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(chapterTitle))
                return false;

            // Normalize Unicode whitespace (NBSP -> space, etc.)
            string NormalizeWs(string s)
            {
                s = s.Replace('\u00A0', ' ').Replace('\u2000', ' ').Replace('\u2001', ' ')
                     .Replace('\u2002', ' ').Replace('\u2003', ' ').Replace('\u2007', ' ')
                     .Replace('\u2008', ' ').Replace('\u2009', ' ').Replace('\u202F', ' ');
                return Regex.Replace(s, @"[ \t]+", " ").Trim();
            }

            string trimmedText = NormalizeWs(text);
            string normalizedTitle = NormalizeWs(chapterTitle);

            // Exact match: content starts with the exact title
            if (trimmedText.StartsWith(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                return true;

            // Get the first few non-empty lines
            var lines = trimmedText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(NormalizeWs)
                                   .Where(s => !string.IsNullOrWhiteSpace(s))
                                   .Take(3)
                                   .ToList();

            if (lines.Count == 0) return false;

            string firstLine = lines[0];

            // First line equals or starts with the title
            if (firstLine.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase)) return true;
            if (firstLine.StartsWith(normalizedTitle, StringComparison.OrdinalIgnoreCase)) return true;

            // Normalize by stripping "Chapter N" / "CHAPTER N" / "N." / "Book X Chapter N" prefixes
            string StripChapterPrefix(string s)
            {
                // Strip "Book X, Chapter N:" etc.
                s = Regex.Replace(s, @"^(Book|BOOK)\s+[IVX\d]+[\s,]*", "", RegexOptions.IgnoreCase);
                // Strip "Chapter N" or "CHAPTER N" with any trailing punctuation/spaces
                s = Regex.Replace(s, @"^(Chapter|CHAPTER)\s+[IVX\d]+[\s\:\.\-\–\—]*", "", RegexOptions.IgnoreCase);
                // Strip "Part N - " etc.
                s = Regex.Replace(s, @"^(Part|PART)\s+[IVX\d]+[\s\:\.\-\–\—]*", "", RegexOptions.IgnoreCase);
                // Strip leading "N. " or "N: " where N is a digit
                s = Regex.Replace(s, @"^\d+[\s\.\:\-\–\—]+", "", RegexOptions.IgnoreCase);
                return s.Trim();
            }

            string strippedTitle = StripChapterPrefix(normalizedTitle);
            string strippedFirstLine = StripChapterPrefix(firstLine);

            // After stripping prefixes, check equality/starts-with
            if (!string.IsNullOrWhiteSpace(strippedTitle))
            {
                if (strippedFirstLine.Equals(strippedTitle, StringComparison.OrdinalIgnoreCase)) return true;
                if (strippedFirstLine.StartsWith(strippedTitle, StringComparison.OrdinalIgnoreCase)) return true;
                if (firstLine.Contains(strippedTitle, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Check all first 3 lines
            foreach (var line in lines)
            {
                if (line.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase)) return true;
                if (line.StartsWith(normalizedTitle, StringComparison.OrdinalIgnoreCase)) return true;

                string strippedLine = StripChapterPrefix(line);
                if (!string.IsNullOrWhiteSpace(strippedTitle))
                {
                    if (strippedLine.Equals(strippedTitle, StringComparison.OrdinalIgnoreCase)) return true;
                    if (strippedLine.StartsWith(strippedTitle, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            // If the first line is clearly a chapter heading (starts with Chapter/CHAPTER/number),
            // and the title is also a chapter heading, consider it redundant
            bool firstLineIsChapterHeading = Regex.IsMatch(firstLine, @"^(Chapter|CHAPTER|Part|PART)\s+[IVX\d]+", RegexOptions.IgnoreCase)
                                          || Regex.IsMatch(firstLine, @"^\d+[\s\.\:\-\–\—]+[A-Za-z]", RegexOptions.IgnoreCase);
            bool titleIsChapterHeading = Regex.IsMatch(normalizedTitle, @"^(Chapter|CHAPTER|Part|PART)\s+[IVX\d]+", RegexOptions.IgnoreCase)
                                      || Regex.IsMatch(normalizedTitle, @"^\d+[\s\.\:\-\–\—]+[A-Za-z]", RegexOptions.IgnoreCase);

            if (firstLineIsChapterHeading && titleIsChapterHeading)
                return true;

            return false;
        }
    }
}
