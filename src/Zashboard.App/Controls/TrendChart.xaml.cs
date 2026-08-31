using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Zashboard.App.Controls;

public sealed partial class TrendChart : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(TrendChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel),
        typeof(string),
        typeof(TrendChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SecondaryLabelProperty = DependencyProperty.Register(
        nameof(SecondaryLabel),
        typeof(string),
        typeof(TrendChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PrimaryStrokeBrushProperty = DependencyProperty.Register(
        nameof(PrimaryStrokeBrush),
        typeof(Brush),
        typeof(TrendChart),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryFillBrushProperty = DependencyProperty.Register(
        nameof(PrimaryFillBrush),
        typeof(Brush),
        typeof(TrendChart),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryStrokeBrushProperty = DependencyProperty.Register(
        nameof(SecondaryStrokeBrush),
        typeof(Brush),
        typeof(TrendChart),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsSecondarySeriesVisibleProperty =
        DependencyProperty.Register(
            nameof(IsSecondarySeriesVisible),
            typeof(bool),
            typeof(TrendChart),
            new PropertyMetadata(false));

    private double[] _primaryValues = [];
    private double[] _secondaryValues = [];
    private bool _formatPerSecond;

    public TrendChart()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string PrimaryLabel
    {
        get => (string)GetValue(PrimaryLabelProperty);
        set => SetValue(PrimaryLabelProperty, value);
    }

    public string SecondaryLabel
    {
        get => (string)GetValue(SecondaryLabelProperty);
        set => SetValue(SecondaryLabelProperty, value);
    }

    public Brush? PrimaryStrokeBrush
    {
        get => (Brush?)GetValue(PrimaryStrokeBrushProperty);
        set => SetValue(PrimaryStrokeBrushProperty, value);
    }

    public Brush? PrimaryFillBrush
    {
        get => (Brush?)GetValue(PrimaryFillBrushProperty);
        set => SetValue(PrimaryFillBrushProperty, value);
    }

    public Brush? SecondaryStrokeBrush
    {
        get => (Brush?)GetValue(SecondaryStrokeBrushProperty);
        set => SetValue(SecondaryStrokeBrushProperty, value);
    }

    public bool IsSecondarySeriesVisible
    {
        get => (bool)GetValue(IsSecondarySeriesVisibleProperty);
        set => SetValue(IsSecondarySeriesVisibleProperty, value);
    }

    public void SetSeries(
        double[] primaryValues,
        double[]? secondaryValues,
        string currentSummary,
        bool formatPerSecond)
    {
        ArgumentNullException.ThrowIfNull(primaryValues);
        _primaryValues = primaryValues;
        _secondaryValues = secondaryValues ?? [];
        _formatPerSecond = formatPerSecond;
        CurrentSummaryText.Text = currentSummary;
        RenderSeries();
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs args)
    {
        RenderSeries();
    }

    private void RenderSeries()
    {
        double width = PlotCanvas.ActualWidth;
        double height = PlotCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        UpdateGridLines(width, height);
        double observedMaximum = _primaryValues
            .Concat(_secondaryValues)
            .DefaultIfEmpty(0)
            .Max();
        double scaleMaximum = NiceMaximum(observedMaximum);
        PrimaryLine.Points = BuildLine(_primaryValues, width, height, scaleMaximum);
        PrimaryArea.Points = BuildArea(_primaryValues, width, height, scaleMaximum);
        SecondaryLine.Points = BuildLine(_secondaryValues, width, height, scaleMaximum);
        ScaleMaximumText.Text = FormatValue(scaleMaximum, _formatPerSecond);
        EmptyChartText.Visibility = _primaryValues.Length == 0 && _secondaryValues.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        string summary = string.IsNullOrWhiteSpace(CurrentSummaryText.Text)
            ? "No current sample."
            : CurrentSummaryText.Text;
        AutomationProperties.SetName(
            this,
            $"{Title}. {summary}. Chart maximum {ScaleMaximumText.Text}.");
    }

    private void UpdateGridLines(double width, double height)
    {
        Line[] lines = [GridLineTop, GridLineUpper, GridLineLower, GridLineBottom];
        for (int index = 0; index < lines.Length; index++)
        {
            double y = height * index / (lines.Length - 1);
            lines[index].X1 = 0;
            lines[index].X2 = width;
            lines[index].Y1 = y;
            lines[index].Y2 = y;
        }
    }

    private static PointCollection BuildLine(
        double[] values,
        double width,
        double height,
        double maximum)
    {
        PointCollection points = [];
        if (values.Length == 1)
        {
            double y = height - (Math.Min(values[0], maximum) / maximum * height);
            points.Add(new Point(0, y));
            points.Add(new Point(width, y));
            return points;
        }

        for (int index = 0; index < values.Length; index++)
        {
            double x = values.Length == 1 ? width : width * index / (values.Length - 1);
            double y = height - (Math.Min(values[index], maximum) / maximum * height);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static PointCollection BuildArea(
        double[] values,
        double width,
        double height,
        double maximum)
    {
        PointCollection points = [];
        if (values.Length == 0)
        {
            return points;
        }

        points.Add(new Point(0, height));
        foreach (Point point in BuildLine(values, width, height, maximum))
        {
            points.Add(point);
        }

        points.Add(new Point(width, height));
        return points;
    }

    private static double NiceMaximum(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        double unit = Math.Pow(1024, Math.Floor(Math.Log(value, 1024)));
        double normalized = value / unit;
        return Math.Pow(2, Math.Ceiling(Math.Log2(normalized))) * unit;
    }

    private static string FormatValue(double bytes, bool perSecond)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        string number = unitIndex == 0
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
        return perSecond ? $"{number} {units[unitIndex]}/s" : $"{number} {units[unitIndex]}";
    }
}
