using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Daftar.Views.Components;

/// <summary>
/// The shell every dialog in the app sits in: a backdrop that closes it, an opaque card, a header
/// with a title, a subtitle and a close button, a scrolling body, and a footer.
///
/// <para>
/// It was written out five times, in four screens, and the copies had already drifted: the corner
/// radius, the header padding and the card width all differ a little between them, and the closer
/// had been given a different glyph in one of them. A dialog is not the place to be original.
/// </para>
///
/// <para>
/// The body and the footer are the screen's own, passed in as attached properties. Everything else
/// is here, once.
/// </para>
/// </summary>
public partial class ModalDialog : UserControl
{
    /// <summary>
    /// Attached rather than declared on the control, so the body can be written as
    /// <c>&lt;components:ModalDialog.Body&gt;</c> rather than through a converter or a template.
    /// </summary>
    public static readonly AttachedProperty<object?> BodyProperty =
        AvaloniaProperty.RegisterAttached<ModalDialog, Control, object?>("Body");

    public static readonly AttachedProperty<object?> FooterProperty =
        AvaloniaProperty.RegisterAttached<ModalDialog, Control, object?>("Footer");

    public static object? GetBody(Control host) => host.GetValue(BodyProperty);

    public static void SetBody(Control host, object? value) => host.SetValue(BodyProperty, value);

    public static object? GetFooter(Control host) => host.GetValue(FooterProperty);

    public static void SetFooter(Control host, object? value) => host.SetValue(FooterProperty, value);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ModalDialog, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<ModalDialog, string?>(nameof(Subtitle));

    /// <summary>
    /// Content for a header that cannot be a title and a line under it.
    /// </summary>
    /// <remarks>
    /// Two of the dialogs put real content beside their title: the round's number, the distributor's
    /// name, a status badge; one of them puts a tab strip there. Putting that in Title would mean a
    /// title that is not a string. It replaces the title and subtitle when set, so a screen uses one
    /// or the other and not both.
    /// </remarks>
    /// Deliberately only an attached property and no instance wrapper: an instance one would be a
    /// second setter for the same AvaloniaProperty, and a view using both spellings would then
    /// fail to compile with "allows multiple assignments".
    public static readonly AttachedProperty<object?> HeaderExtraProperty =
        AvaloniaProperty.RegisterAttached<ModalDialog, Control, object?>("HeaderExtra");

    public static object? GetHeaderExtra(Control host) => host.GetValue(HeaderExtraProperty);

    public static void SetHeaderExtra(Control host, object? value) =>
        host.SetValue(HeaderExtraProperty, value);

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ModalDialog, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<double> DialogWidthProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(DialogWidth), 520d);

    /// <summary>How tall the scrolling body may get before it scrolls instead of growing.</summary>
    public static readonly StyledProperty<double> BodyMaxHeightProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(BodyMaxHeight), 460d);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Optional. The line under the title that says what the dialog is for.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// Runs from both the backdrop and the ✕. There is no way to open a dialog with no way out of
    /// it, which is why this has no default.
    /// </summary>
    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public double DialogWidth
    {
        get => GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public double BodyMaxHeight
    {
        get => GetValue(BodyMaxHeightProperty);
        set => SetValue(BodyMaxHeightProperty, value);
    }

    /// <summary>
    /// Whether clicking outside the card dismisses the dialog. Defaults to false, because losing a
    /// half-typed form to a stray click is worse than needing the ✕. Every screen in this app
    /// closes on the backdrop, so every one of them sets this.
    /// </summary>
    public static readonly StyledProperty<bool> BackdropClosesProperty =
        AvaloniaProperty.Register<ModalDialog, bool>(nameof(BackdropCloses));

    public bool BackdropCloses
    {
        get => GetValue(BackdropClosesProperty);
        set => SetValue(BackdropClosesProperty, value);
    }

    /// <summary>
    /// Paints the header in the app's blue instead of the neutral card fill.
    /// </summary>
    /// <remarks>
    /// There are two kinds of dialog here and they are not the same: a form you fill in, whose
    /// header is a caption like any other; and a job the user has to finish, like settling a
    /// distributor's round, whose header is the headline and is tinted to say so. Both appeared
    /// with their own hand-written blue header until this existed.
    /// </remarks>
    public static readonly StyledProperty<bool> PrimaryHeaderProperty =
        AvaloniaProperty.Register<ModalDialog, bool>(nameof(PrimaryHeader));

    public bool PrimaryHeader
    {
        get => GetValue(PrimaryHeaderProperty);
        set => SetValue(PrimaryHeaderProperty, value);
    }

    /// <summary>
    /// A fixed card height, for the dialogs that hold a lot. Left unset the card is as tall as its
    /// content, which is right for a form and wrong for a five-step wizard whose steps have to
    /// stay in the same place as it moves between them.
    /// </summary>
    public static readonly StyledProperty<double> DialogHeightProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(DialogHeight), double.NaN);

    public double DialogHeight
    {
        get => GetValue(DialogHeightProperty);
        set => SetValue(DialogHeightProperty, value);
    }

    public ModalDialog()
    {
        InitializeComponent();

        // Escape is how everyone tries to leave a modal, and the status bar advertises keyboard
        // shortcuts, so a dialog you can only leave with the mouse is out of step with the rest of
        // the app. There is no way to open a dialog with no way out of it, which is what the
        // CloseCommand having no default is for.
        //
        // Tunnelling, not the plain KeyDown event. A plain handler waits for the bubble to arrive,
        // which means every control between the caret and the dialog gets to look at the key first,
        // and a TextBox that decides Escape is its own is enough to make the dialog unclosable. A
        // modal that cannot be dismissed by Escape is not a modal; it goes first, on the way down.
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Focus follows the dialog, but only once it is actually on screen.
        //
        // Every screen in this app builds its dialogs up front and binds IsVisible to a flag, so a
        // dialog is attached to the tree hours before anyone opens it. Reacting to
        // AttachedToVisualTree therefore runs while the dialog is still hidden, when there is
        // nothing to focus: the first version of this did exactly that and the caret never moved.
        // The headless test agreed it worked, because it built its dialog already visible.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                FocusFirstField();
        };

        // Every screen here is a Grid with a filter row and a content row, and a dialog is added
        // as a bare child of it. With no Grid.Row that lands the dialog in row 0, the Auto row, and
        // it takes that row height: a band of backdrop across the top, the card clipped after its
        // title, and the data grid painted back over the rest of the screen. Measured at 40 pixels
        // in an 800-pixel window before this.
        //
        // Spanning is set here rather than as Grid.RowSpan on each of the six call sites, because
        // the call sites are exactly what got it wrong: a span written on a host is one more thing
        // the next dialog has to remember, and forgetting it reproduces the same clipped card
        // without a single failing test.
        AttachedToVisualTree += (_, _) => SpanTheHostRows();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || CloseCommand is not { } close)
            return;

        // Marked handled so the key does not also reach the screen behind and do whatever it does
        // there while a modal is up.
        e.Handled = true;
        close.Execute(null);
    }

    /// <summary>
    /// Makes the dialog cover every row of the Grid it was added to.
    /// </summary>
    /// <remarks>
    /// Harmless where the host is not a Grid, and idempotent, so it does not matter whether a
    /// screen also spells out a span.
    /// </remarks>
    private void SpanTheHostRows()
    {
        if (Parent is Grid host)
            Grid.SetRowSpan(this, host.RowDefinitions.Count);
    }

    /// <summary>
    /// Puts the caret in the first field the dialog offers.
    /// </summary>
    /// <remarks>
    /// Posted rather than done inline: at the moment this runs the dialog has just been attached, so
    /// its content control has not realised its child yet and there is nothing to focus. Looking for
    /// the first focusable descendant is better than focusing the card, which would leave Tab to
    /// walk the whole thing from the top, including the close button.
    /// </remarks>
    private void FocusFirstField()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = this.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c is not Button);

            (target ?? this).Focus();
        });
    }
}