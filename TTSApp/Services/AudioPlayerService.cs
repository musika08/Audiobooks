using System;
using System.IO;
using System.Windows.Threading;
using NAudio.Wave;

namespace TTSApp.Services
{
    /// <summary>
    /// Thin wrapper around NAudio that keeps playback state in one place.
    /// UI classes subscribe to <see cref="StateChanged"/> and <see cref="PositionChanged"/>
    /// instead of poking NAudio controls directly.
    /// </summary>
    public sealed class AudioPlayerService : IDisposable
    {
        private WaveOutEvent? _waveOut;
        private AudioFileReader? _reader;
        private readonly DispatcherTimer _positionTimer;
        private bool _isDisposed;

        public AudioPlayerService()
        {
            _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                OnPositionTick,
                Dispatcher.CurrentDispatcher)
            {
                IsEnabled = false
            };
        }

        public string? CurrentFile { get; private set; }

        public bool IsPlaying => !_isDisposed && _waveOut?.PlaybackState == PlaybackState.Playing;

        public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;

        public TimeSpan TotalTime => _reader?.TotalTime ?? TimeSpan.Zero;

        public bool HasMedia => _reader != null;

        public event EventHandler? StateChanged;

        public event EventHandler? PositionChanged;

        public void Play(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            Stop();

            CurrentFile = filePath;
            _reader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_reader);
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Play();
            _positionTimer.Start();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            _waveOut?.Pause();
            _positionTimer.Stop();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Resume()
        {
            _waveOut?.Play();
            _positionTimer.Start();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Toggle()
        {
            if (IsPlaying)
                Pause();
            else if (_waveOut != null)
                Resume();
        }

        public void Seek(TimeSpan position)
        {
            if (_reader == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _reader.TotalTime) position = _reader.TotalTime;
            _reader.CurrentTime = position;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _positionTimer.Stop();
            if (_waveOut != null)
            {
                _waveOut.PlaybackStopped -= OnPlaybackStopped;
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            _reader?.Dispose();
            _reader = null;
            CurrentFile = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _positionTimer.Stop();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnPositionTick(object? sender, EventArgs e)
        {
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _positionTimer.Stop();
            Stop();
            _positionTimer.Tick -= OnPositionTick;
        }
    }
}
