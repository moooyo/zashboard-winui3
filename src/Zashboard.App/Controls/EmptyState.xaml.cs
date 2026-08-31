using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zashboard.App.Controls;

public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata("\uE946"));

    public static readonly DependencyProperty StateTitleProperty = DependencyProperty.Register(
        nameof(StateTitle),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty));

    public EmptyState()
    {
        InitializeComponent();
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public string StateTitle
    {
        get => (string)GetValue(StateTitleProperty);
        set => SetValue(StateTitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
