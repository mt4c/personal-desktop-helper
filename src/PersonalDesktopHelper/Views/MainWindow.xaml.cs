using System.Windows;
using System.Windows.Input;
using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Views;

public partial class MainWindow : Window
{
    private readonly ChatViewModel _chat;
    private readonly Action _openOptions;

    public MainWindow(ChatViewModel chat, Action openOptions)
    {
        _chat = chat;
        _openOptions = openOptions;
        InitializeComponent();
        DataContext = chat;
        Loaded += (_, _) => TranscriptScroll.ScrollToEnd();
        _chat.TranscriptChanged += OnTranscriptChanged;
        Closed += (_, _) => _chat.TranscriptChanged -= OnTranscriptChanged;
    }

    private void OnTranscriptChanged(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd());
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_chat.CanSend)
        {
            await _chat.SendAsync();
            PromptTextBox.Focus();
        }
    }

    private async void OnPromptKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_chat.CanSend)
            {
                await _chat.SendAsync();
                PromptTextBox.Focus();
            }
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _chat.RequestStop();
    private async void OnNewChatClick(object sender, RoutedEventArgs e) => await _chat.NewChatAsync();
    private void OnOptionsClick(object sender, RoutedEventArgs e) => _openOptions();
}