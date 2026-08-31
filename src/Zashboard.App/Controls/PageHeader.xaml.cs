using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zashboard.App.Controls;

public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public PageHeader()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Visibility SubtitleVisibility => string.IsNullOrWhiteSpace(Subtitle)
        ? Visibility.Collapsed
        : Visibility.Visible;

    private static void OnSubtitleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is PageHeader header)
        {
            header.Bindings.Update();
        }
    }
}
