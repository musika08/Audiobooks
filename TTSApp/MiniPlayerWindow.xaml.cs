using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NAudio.Wave;

namespace TTSApp
{
    public partial class MiniPlayerWindow : Window
    {
        private readonly MainWindow _main;

        public MiniPlayerWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        public void UpdateState(bool isPlaying, string title, TimeSpan current, TimeSpan total)
        {
            Dispatcher.Invoke(() =>
            {
                TxtMiniTitle.Text = title;
                BtnMiniPlay.Content = isPlaying ? "⏸" : "▶";
                SliderMiniSeek.Maximum = total.TotalSeconds;
                SliderMiniSeek.Value = current.TotalSeconds;
                LblMiniTime.Text = $"{current.Minutes:D2}:{current.Seconds:D2} / {total.Minutes:D2}:{total.Seconds:D2}";
            });
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e) => _main.MiniRewind();
        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => _main.MiniPlayPause();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => _main.MiniStop();
        private void BtnForward_Click(object sender, RoutedEventArgs e) => _main.MiniForward();
        private void SliderSeek_DragCompleted(object sender, DragCompletedEventArgs e) => _main.MiniSeek(SliderMiniSeek.Value);
    }
}
