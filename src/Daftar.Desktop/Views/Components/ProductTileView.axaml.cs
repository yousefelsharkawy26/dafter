using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daftar.Views.Components;

/// <summary>
/// One square in the product grid, shared by the sell and receive screens. See
/// <see cref="ProductTileView"/> for the square itself; this is the grid it sits in.
/// </summary>
public partial class ProductTileView : UserControl
{
    public ProductTileView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}