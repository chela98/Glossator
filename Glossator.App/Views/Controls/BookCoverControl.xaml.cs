using System.Windows;
using System.Windows.Controls;

namespace Glossator.App.Views.Controls;

public partial class BookCoverControl : UserControl
{
    public static readonly DependencyProperty CoverPathProperty = DependencyProperty.Register(
        nameof(CoverPath), typeof(string), typeof(BookCoverControl), new PropertyMetadata(null));

    public static readonly DependencyProperty MonogramProperty = DependencyProperty.Register(
        nameof(Monogram), typeof(string), typeof(BookCoverControl), new PropertyMetadata("EP"));

    public static readonly DependencyProperty FallbackHexProperty = DependencyProperty.Register(
        nameof(FallbackHex), typeof(string), typeof(BookCoverControl), new PropertyMetadata("#1E3A8A"));

    public static readonly DependencyProperty CoverCornerRadiusProperty = DependencyProperty.Register(
        nameof(CoverCornerRadius), typeof(CornerRadius), typeof(BookCoverControl), new PropertyMetadata(new CornerRadius(6)));

    public static readonly DependencyProperty MonogramFontSizeProperty = DependencyProperty.Register(
        nameof(MonogramFontSize), typeof(double), typeof(BookCoverControl), new PropertyMetadata(18.0));

    public BookCoverControl()
    {
        InitializeComponent();
    }

    public string? CoverPath
    {
        get => (string?)GetValue(CoverPathProperty);
        set => SetValue(CoverPathProperty, value);
    }

    public string Monogram
    {
        get => (string)GetValue(MonogramProperty);
        set => SetValue(MonogramProperty, value);
    }

    public string FallbackHex
    {
        get => (string)GetValue(FallbackHexProperty);
        set => SetValue(FallbackHexProperty, value);
    }

    public CornerRadius CoverCornerRadius
    {
        get => (CornerRadius)GetValue(CoverCornerRadiusProperty);
        set => SetValue(CoverCornerRadiusProperty, value);
    }

    public double MonogramFontSize
    {
        get => (double)GetValue(MonogramFontSizeProperty);
        set => SetValue(MonogramFontSizeProperty, value);
    }
}
