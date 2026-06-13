using System.ComponentModel;

namespace TTSApp
{
    public enum ConvertState { None, Queued, Converting, Done, Failed }

    public class ChapterItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private int _index;
        private ConvertState _status = ConvertState.None;

        public string Title { get; set; } = "";
        public string Content { get; set; } = "";

        public int Index
        {
            get => _index;
            set
            {
                _index = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number)));
            }
        }

        public int Number => _index + 1;

        // Per-chapter voice: -1 = use the global voice; otherwise a speaker index. Runtime-only.
        public int VoiceOverride { get; set; } = -1;

        // For URL-imported chapters: the page it came from, and the discovered "next chapter" link.
        public string? SourceUrl { get; set; }
        public string? NextUrl { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public ConvertState Status
        {
            get => _status;
            set
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
            }
        }

        // Small indicator shown in the chapter list.
        public string StatusGlyph => _status switch
        {
            ConvertState.Queued => "⋯",
            ConvertState.Converting => "▶",
            ConvertState.Done => "✓",
            ConvertState.Failed => "✗",
            _ => ""
        };

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
