using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daftar.Views.Components;

/// <summary>
/// The grid of product squares, shared by the sell and receive screens. The square itself is
/// <see cref="ProductTileView"/>.
///
/// <para>
/// Deliberately a control rather than a template: both screens had this block copied verbatim, and
/// a template that lives in two places is one that will be edited in one of them.
/// </para>
///
/// <para>
/// It also says what to show when there is nothing, which is the other thing both screens had been
/// writing out — including a hint that had drifted by one vowel between them.
/// </para>
/// </summary>
public partial class ProductTileGrid : UserControl
{
    /// <summary>
    /// A styled property rather than a plain one, so the screen can bind to it. Avalonia compiles
    /// the bindings in these views, and a compiled binding needs a settable AvaloniaProperty; a
    /// plain CLR property is invisible to it.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ProductTileGrid, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Whether there is anything to show. Kept here rather than bound as a count so the "nothing to
    /// see" wording cannot drift away from the thing that decides there is nothing to see.
    /// </summary>
    public bool HasItems => Count(ItemsSource) > 0;

    public ProductTileGrid() => InitializeComponent();

    private static int Count(IEnumerable? items)
    {
        // The screens hand over an ObservableCollection, which is a collection, so the common path
        // is O(1). Anything else is walked just far enough to know whether there is anything.
        if (items is null)
            return 0;

        if (items is ICollection c)
            return c.Count;

        if (items is IEnumerable e)
            return CountByWalking(e);

        return 0;
    }

    /// <summary>
    /// Only reached for a lazy or non-collection sequence, which this never is: the screens hand
    /// over an ObservableCollection. Walking it would enumerate the products on every layout pass,
    /// so the count stops at the first item instead.
    /// </summary>
    private static int CountByWalking(IEnumerable items)
    {
        var e = items.GetEnumerator();
        try
        {
            return e.MoveNext() ? 1 : 0;
        }
        finally
        {
            (e as System.IDisposable)?.Dispose();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ItemsSourceProperty)
            return;

        // The screens hand over the same collection every time and filter by clearing and refilling
        // it, so the reference changing is not the event that matters. Watching the collection is
        // what makes the empty state come back when the last product is typed away: a grid that
        // only recomputes on rebinding stays blank forever after its first filter.
        if (change.OldValue is System.Collections.Specialized.INotifyCollectionChanged old)
            old.CollectionChanged -= OnItemsChanged;

        if (change.NewValue is System.Collections.Specialized.INotifyCollectionChanged added)
            added.CollectionChanged += OnItemsChanged;

        RaisePropertyChanged(HasItemsProperty, oldValue: HasItems, newValue: HasItems);
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RaisePropertyChanged(HasItemsProperty, oldValue: HasItems, newValue: HasItems);

    public static readonly DirectProperty<ProductTileGrid, bool> HasItemsProperty =
        AvaloniaProperty.RegisterDirect<ProductTileGrid, bool>(nameof(HasItems), r => r.HasItems);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}