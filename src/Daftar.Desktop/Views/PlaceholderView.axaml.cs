using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daftar.Views;

/// <summary>
/// Shown for a page that has no dedicated screen yet. Says what the page will do rather than
/// pretending, so an unfinished page is never mistaken for a broken one.
///
/// Everything is bound to <see cref="Daftar.ViewModels.TabViewModel"/>; there is deliberately no
/// code that reaches for named elements on DataContextChanged, because that depends on
/// initialisation order and throws when the DataContext arrives first.
/// </summary>
public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}