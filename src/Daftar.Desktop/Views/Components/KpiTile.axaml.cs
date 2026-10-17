using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml;

namespace Daftar.Views.Components;

/// <summary>
/// One figure with its label: the card every dashboard in this app puts its headline numbers in.
///
/// <para>
/// It was written out twenty-eight times across three screens, in three different looks: the
/// wrapper was <c>card</c> in one, <c>surface</c> in the next and <c>panel</c> in the third, with
/// three different paddings and two different label styles. Which means a number in the reports
/// did not look like the same number in the sessions, and changing the type size meant finding
/// twenty-eight places.
/// </para>
///
/// <para>
/// Icon and Detail are optional and collapse when empty, because two of the three screens use the
/// two-line form and one uses three.
/// </para>
/// </summary>
public partial class KpiTile : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<KpiTile, string?>(nameof(Label));

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<KpiTile, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<KpiTile, string?>(nameof(Value));

    /// <summary>
    /// Optional. Green for a gain, red for a loss, the app's blue for a neutral headline. Left
    /// null the value takes the ordinary text colour, which is the right default: a figure is not
    /// good news or bad news by being a figure.
    /// </summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<KpiTile, IBrush?>(nameof(ValueBrush));

    /// <summary>
    /// The line under the value. An object rather than a string so a screen that needs a badge
    /// there can put one.
    /// </summary>
    public static readonly StyledProperty<object?> DetailProperty =
        AvaloniaProperty.Register<KpiTile, object?>(nameof(Detail));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public object? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public KpiTile() => InitializeComponent();

    /// <summary>
    /// Paints the figure with the brush it was given, or with the theme's ordinary ink when it was
    /// given none.
    ///
    /// <para>
    /// Not <c>Foreground="{Binding ValueBrush}"</c>, which is what this was: null is still a value,
    /// so the binding wrote null over whatever the theme had put there and the number vanished from
    /// every tile that was neither a gain nor a loss. The tiles either side of the blank one looked
    /// right, so a look at the screen did not find it.
    /// </para>
    ///
    /// <para>
    /// The ordinary ink is asked for by name rather than written down, so the figure follows the
    /// theme. A name that cannot be resolved leaves the figure as it is — wrong colour at worst,
    /// never nothing.
    /// </para>
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ValueBrushProperty) return;
        if (this.FindControl<TextBlock>("ValueText") is not { } figure) return;

        if (change.GetNewValue<IBrush?>() is { } brush)
        {
            figure.Foreground = brush;
            return;
        }

        if (this.TryFindResource("TextFillColorPrimaryBrush", out var ink) && ink is IBrush ordinary)
            figure.Foreground = ordinary;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}