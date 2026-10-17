using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Daftar.Views.Components;

/// <summary>
/// A search field with the magnifier drawn inside it.
///
/// <para>
/// It was written out ten times across six screens, in two sizes, and the two sizes had drifted
/// further apart than that: the big one used the theme's search height and a 16pt icon, the small
/// ones hard-coded 38 and a 15pt icon. A search box that is a few pixels taller on one screen
/// than another reads as a mistake even when it is only padding.
/// </para>
///
/// <para>
/// <see cref="Large"/> is the only difference between the two forms, and it exists because the
/// till wants a target you can hit with a barcode scanner and a reports filter does not.
/// </para>
/// </summary>
public partial class SearchBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SearchBox, string?>(nameof(Text));

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<SearchBox, string?>(nameof(Placeholder));

    /// <summary>
    /// The tall form used on the till. Binding both ways: the barcode reader writes the field from
    /// outside, and a typed search has to come back the other way.
    /// </summary>
    public static readonly StyledProperty<bool> LargeProperty =
        AvaloniaProperty.Register<SearchBox, bool>(nameof(Large));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public bool Large
    {
        get => GetValue(LargeProperty);
        set => SetValue(LargeProperty, value);
    }

    public SearchBox() => InitializeComponent();

    /// <summary>
    /// Puts the caret in the field.
    ///
    /// <para>
    /// The till focuses its search box on every screen change, so the field has to be reachable by
    /// something other than reaching into this control's template. Before this was a component it
    /// was a TextBox named "SearchBox" and <c>FindControl&lt;TextBox&gt;</c> found it; wrapping it
    /// silently broke that, and a search box the cashier cannot put the caret in is a search box
    /// they have to click.
    /// </para>
    /// </summary>
    /// <summary>
    /// Resolved on each call, not cached at load: a UserControl's visual tree is not populated
    /// until it is attached, so a TextBox looked up during construction comes back null.
    /// </summary>
    public void FocusSearch() => Inner?.Focus();

    /// <summary>
    /// Resolved on each call, not cached at load: a UserControl's visual tree is not populated
    /// until it is attached, so a TextBox looked up during construction comes back null.
    /// </summary>
    private TextBox? Inner => this.FindControl<TextBox>("Field");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}