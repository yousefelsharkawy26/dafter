using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Daftar.ViewModels;

/// <summary>Which screen is showing the tile. They differ in the flags, not in the layout.</summary>
public enum ProductTileVariant
{
    /// <summary>Selling: warns about low stock and about running out, and does not offer "add".</summary>
    Sell,

    /// <summary>Receiving: shows what is on hand and offers to add a line.</summary>
    Receive,
}

/// <summary>One unit of a product, flattened for the tile.</summary>
public sealed record ProductTileUnit(
    int ProductUnitId,
    string UnitName,
    decimal UnitFactor,
    decimal Price,
    decimal PurchasePrice,
    decimal OnHand);

/// <summary>
/// One square in the product grid.
///
/// <para>
/// The sell and receive screens each had their own item type and their own copy of the tile markup,
/// so a change to one was a change to one screen. This is the shape both of them present, which is
/// what lets <c>ProductTileView</c> be a single control rather than a template that has to be
/// copied and kept in step.
/// </para>
///
/// <para>
/// It holds no cart logic and no database knowledge: the screen builds these from whatever its
/// own product list already knows, and supplies the command to run when a tile is tapped. Switching
/// the unit re-derives everything that depends on it, which is why the numbers live on the unit and
/// the text is produced from them rather than copied once at load.
/// </para>
/// </summary>
public sealed partial class ProductTileItem : ObservableObject
{
    private readonly Func<ProductTileUnit, string> _priceFormatter;
    private readonly Action<ProductTileUnit>? _unitSelected;

    public ProductTileItem(
        object source,
        int productId,
        string sku,
        string name,
        ProductTileVariant variant,
        IEnumerable<ProductTileUnit> units,
        Func<ProductTileUnit, string> priceFormatter,
        Action<ProductTileUnit>? unitSelected,
        ICommand selectCommand)
    {
        Source = source;
        ProductId = productId;
        Sku = sku;
        Name = name;
        Variant = variant;
        _priceFormatter = priceFormatter;
        _unitSelected = unitSelected;
        SelectCommand = selectCommand;

        foreach (var unit in units)
            Units.Add(unit);

        SelectedUnit = Units.FirstOrDefault();
    }

    /// <summary>
    /// The screen's own product object behind this tile.
    ///
    /// <para>
    /// The units are copied rather than shared, so the screen has to be told when the cashier picks
    /// a different one: without that the cart would add the unit the product happened to be built
    /// with. <see cref="UnitSelected"/> is that handshake.
    /// </para>
    /// </summary>
    public object Source { get; }

    /// <summary>Raised after the cashier picks a unit, so the screen can move its own selection too.</summary>
    public event Action<ProductTileUnit>? UnitSelected;

    public int ProductId { get; }

    public string Sku { get; }

    public string Name { get; }

    public ProductTileVariant Variant { get; }

    public ObservableCollection<ProductTileUnit> Units { get; } = [];

    /// <summary>Tapping the square. Selling and receiving do different things, so this varies.</summary>
    public ICommand SelectCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitName))]
    [NotifyPropertyChangedFor(nameof(UnitSuffix))]
    [NotifyPropertyChangedFor(nameof(HasStock))]
    [NotifyPropertyChangedFor(nameof(IsLowStock))]
    [NotifyPropertyChangedFor(nameof(PriceText))]
    [NotifyPropertyChangedFor(nameof(OnHandText))]
    private ProductTileUnit? _selectedUnit;

    public bool HasMultipleUnits => Units.Count > 1;

    partial void OnSelectedUnitChanged(ProductTileUnit? value)
    {
        if (value is not null)
        {
            _unitSelected?.Invoke(value);
            UnitSelected?.Invoke(value);
        }
    }

    public string UnitName => SelectedUnit?.UnitName ?? string.Empty;

    /// <summary>
    /// "/ كجم", said as one string rather than through a StringFormat.
    /// </summary>
    /// <remarks>
    /// Written as <c>StringFormat='/ {}{0}'</c> it rendered as nothing: the escaped placeholder
    /// in a format that also carries a literal defeats the parser, and an empty string beside the
    /// price reads as "no unit" rather than as a broken binding. Every colour and size test passed
    /// with it; it was obvious the moment the screen was photographed.
    /// </remarks>
    public string UnitSuffix => $"/ {UnitName}";

    public decimal OnHand => SelectedUnit?.OnHand ?? 0m;

    /// <summary>Tile stays tappable only while there is something to sell or to receive.</summary>
    public bool HasStock => OnHand > 0m;

    /// <summary>Warn before the shelf surprises them at the till.</summary>
    public bool IsLowStock => HasStock && OnHand <= 5m;

    public string PriceText => SelectedUnit is { } unit ? _priceFormatter(unit) : string.Empty;

    public string OnHandText => Variant == ProductTileVariant.Receive
        ? string.Format(CultureInfo.InvariantCulture, "المخزون: {0:N0}", OnHand)
        : string.Format(CultureInfo.InvariantCulture, "متاح: {0:N0}", OnHand);

    public string ToolTipText => $"{Name} ({UnitName}) — {Sku}";

    // ---- what each screen shows ----

    /// <summary>Selling warns; receiving reports. They are different facts, not different styling.</summary>
    public bool ShowsStockWarnings => Variant == ProductTileVariant.Sell;

    public bool ShowsOnHandChip => Variant == ProductTileVariant.Receive;

    /// <summary>
    /// "＋ إضافة" on the receive square. It is a label, not a button: tapping the square already
    /// does that, and a second button that does the same thing is a second thing to mis-hit.
    /// </summary>
    public bool ShowsAddHint => Variant == ProductTileVariant.Receive;
}