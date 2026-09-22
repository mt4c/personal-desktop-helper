using System.ComponentModel;

namespace PersonalDesktopHelper.Copilot;

public sealed class ChatMessage(string role, string content) : INotifyPropertyChanged
{
    private string _content = content;

    public string Role { get; } = role;

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
