using System.ComponentModel;
using System.Runtime.CompilerServices;
using Glossator.App.Infrastructure;
using Glossator.Core.Models.Epub;

namespace Glossator.App.ViewModels;

public class ChapterItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private ChapterTranslationState _state;

    public int OrderNumber { get; init; }
    public required EpubItem Item { get; init; }
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Item.Title) ? Item.Title : System.IO.Path.GetFileName(Item.Href);
    public string Href => Item.Href;
    public string RoleText => Item.Role.ToString();

    public ChapterTranslationState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadge));
            }
        }
    }

    public string StatusBadge => State switch
    {
        ChapterTranslationState.Completed => "✅ Tamamlandı",
        ChapterTranslationState.Partial => "⏳ Yarım (.part)",
        _ => "⬜ Bekliyor"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
