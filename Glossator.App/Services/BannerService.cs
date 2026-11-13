using System.Windows.Input;
using Glossator.App.ViewModels;
using Wpf.Ui.Controls;

namespace Glossator.App.Services;

/// <summary>
/// Üst bildirim şeridi (InfoBar) durumunu ve isteğe bağlı hızlı aksiyon butonunu yöneten servis.
/// </summary>
public sealed class BannerService : ObservableObject
{
    private bool _isOpen;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private InfoBarSeverity _severity = InfoBarSeverity.Informational;
    private string? _actionLabel;
    private ICommand? _actionCommand;

    public BannerService()
    {
        DismissCommand = new RelayCommand(Dismiss);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public InfoBarSeverity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    public string? ActionLabel
    {
        get => _actionLabel;
        private set
        {
            if (SetProperty(ref _actionLabel, value))
            {
                OnPropertyChanged(nameof(HasAction));
            }
        }
    }

    public ICommand? ActionCommand
    {
        get => _actionCommand;
        private set
        {
            if (SetProperty(ref _actionCommand, value))
            {
                OnPropertyChanged(nameof(HasAction));
            }
        }
    }

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel) && ActionCommand != null;

    public ICommand DismissCommand { get; }

    public void Show(
        string title,
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational,
        string? actionLabel = null,
        Action? onAction = null)
    {
        Title = title;
        Message = message;
        Severity = severity;

        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction != null)
        {
            ActionLabel = actionLabel;
            ActionCommand = new RelayCommand(() =>
            {
                onAction();
                IsOpen = false;
            });
        }
        else
        {
            ActionLabel = null;
            ActionCommand = null;
        }

        IsOpen = true;
    }

    public void Dismiss() => IsOpen = false;
}
