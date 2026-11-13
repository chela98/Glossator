using System.ComponentModel;
using System.Runtime.CompilerServices;
using Glossator.Core.Models.Glossary;

namespace Glossator.App.ViewModels;

public class GlossaryTermViewModel : INotifyPropertyChanged
{
    private int _id;
    private string _englishTerm = string.Empty;
    private string? _turkishTerm;
    private GlossingPolicy _policy = GlossingPolicy.ProgressiveGloss;
    private string? _englishDefinition;
    private bool _isReviewed;
    private string? _sourceBook;

    public int Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string EnglishTerm
    {
        get => _englishTerm;
        set => SetField(ref _englishTerm, value);
    }

    public string? TurkishTerm
    {
        get => _turkishTerm;
        set => SetField(ref _turkishTerm, value);
    }

    public GlossingPolicy Policy
    {
        get => _policy;
        set => SetField(ref _policy, value);
    }

    public string? EnglishDefinition
    {
        get => _englishDefinition;
        set => SetField(ref _englishDefinition, value);
    }

    public bool IsReviewed
    {
        get => _isReviewed;
        set => SetField(ref _isReviewed, value);
    }

    public string? SourceBook
    {
        get => _sourceBook;
        set => SetField(ref _sourceBook, value);
    }

    public GlossaryEntry ToModel() => new()
    {
        Id = Id,
        EnglishTerm = EnglishTerm,
        TurkishTerm = TurkishTerm,
        Policy = Policy,
        EnglishDefinition = EnglishDefinition,
        IsReviewed = IsReviewed,
        SourceBook = SourceBook
    };

    public static GlossaryTermViewModel FromModel(GlossaryEntry entry) => new()
    {
        Id = entry.Id,
        EnglishTerm = entry.EnglishTerm,
        TurkishTerm = entry.TurkishTerm,
        Policy = entry.Policy,
        EnglishDefinition = entry.EnglishDefinition,
        IsReviewed = entry.IsReviewed,
        SourceBook = entry.SourceBook
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
