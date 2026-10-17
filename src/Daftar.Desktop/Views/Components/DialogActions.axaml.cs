using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daftar.Views.Components;

/// <summary>
/// The button row at the bottom of a dialog: a busy bar, Cancel, and the one action that saves.
///
/// <para>
/// It was written out three times, identical down to the spacing, and the copies had already
/// drifted in the details — the padding and the height attributes were in a different order in
/// each, and one of the three showed its save label as a literal where the others bound it. A
/// dialog's Save button is the thing the cashier is looking for; it should look the same in all of
/// them.
/// </para>
///
/// <para>
/// The busy bar lives here rather than in each screen because "saving, do not press it again" is
/// part of what this row means, not decoration on top of it.
/// </para>
/// </summary>
public partial class DialogActions : UserControl
{
    /// <summary>
    /// Shows the busy bar. Bind this to the same state that disables saving, so the two cannot
    /// disagree about whether the press is going through.
    /// </summary>
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<DialogActions, bool>(nameof(IsBusy));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DialogActions, ICommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<DialogActions, ICommand?>(nameof(SaveCommand));

    /// <summary>
    /// What the save button says. Bound by the screens that change it: saving a party says
    /// "إضافة" the first time and "تحديث" the second.
    /// </summary>
    public static readonly StyledProperty<string?> SaveTextProperty =
        AvaloniaProperty.Register<DialogActions, string?>(nameof(SaveText));

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ICommand? SaveCommand
    {
        get => GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    public string? SaveText
    {
        get => GetValue(SaveTextProperty);
        set => SetValue(SaveTextProperty, value);
    }

    public DialogActions() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}