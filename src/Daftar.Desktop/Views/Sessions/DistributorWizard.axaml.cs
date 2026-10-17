using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daftar.Views.Sessions;

/// <summary>
/// Settling a distributor round, as a five-step dialog.
///
/// <para>
/// It was the bulk of SessionsView: 550 lines of steps sitting behind a dialog shell that had been
/// written out by hand and differed from every other dialog in the app. It is a control now, so the
/// file it used to live in says what SessionsView is.
/// </para>
///
/// <para>
/// The shell is <see cref="Components.ModalDialog"/> with <c>PrimaryHeader</c>, the same one the
/// forms use. DataContext flows in from the page, so nothing here has to be handed over.
/// </para>
/// </summary>
public partial class DistributorWizard : UserControl
{
    public DistributorWizard() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}