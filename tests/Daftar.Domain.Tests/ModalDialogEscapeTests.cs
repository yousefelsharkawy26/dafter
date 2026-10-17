using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A modal dialog covers the screen, can be got out of, and takes the keystrokes meant for it.
/// </summary>
///
/// <para>
/// The shell looked complete: a backdrop that closes it, a card, a header with an ✕. It was not.
/// Four things, all of them in the shared component, so all thirty-nine dialogs in the app share
/// them.
/// </para>
///
/// <para>
/// It was 90 pixels tall. Every screen that hosts a dialog is a Grid with a filter row and a
/// content row, and the dialog was added as a bare child: with no <c>Grid.Row</c> it landed in row
/// zero, the Auto row, and took that row's height. The result was a band of backdrop across the top,
/// the card clipped after its title, and the data grid painted back over the rest of the screen.
/// Found by opening one in the running application, not by reading it: no test rendered a dialog
/// inside the layout its screen actually uses.
/// </para>
///
/// <para>
/// The rest are not visible either. There was no way out with the keyboard. The caret never moved
/// into the dialog, so the user typed and nothing arrived. And the backdrop was made unclickable on
/// the screens that asked not to close on a click, which meant the click fell through it onto the
/// screen behind, on the button the user had just been looking at.
/// </para>
/// </summary>
public sealed class ModalDialogEscapeTests
{
    private static void Show(Func<ModalDialog> build, Action<ModalDialog> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1200;
            window.Height = 800;

            var dialog = build();
            window.Content = dialog;
            window.Show();
            AvaloniaUi.Settle();

            check(dialog);
        });
    }

    private static Button BackdropOf(ModalDialog dialog) => dialog.GetVisualDescendants()
        .OfType<Button>()
        .First(b => b.Background is ISolidColorBrush brush && brush.Color.A > 0
                    && brush.Color.R < 0.1 && brush.Color.G < 0.1);

    private static ModalDialog A_Form(RelayCommand onClose)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "الاسم", Classes = { "caption" } });
        body.Children.Add(new TextBox { Name = "FirstField" });
        body.Children.Add(new TextBox { Name = "SecondField" });

        var dialog = new ModalDialog
        {
            Title = "عميل جديد",
            CloseCommand = onClose,
            BackdropCloses = true,
        };

        ModalDialog.SetBody(dialog, body);

        return dialog;
    }

    [Fact]
    public void Escape_From_A_Field_Inside_The_Dialog_Closes_It()
    {
        // Raised on the focused field, not on the dialog. That is where a key press actually starts,
        // and it is the only version of this that tells the truth: the first one raised the event on
        // the dialog itself, passed against a handler that never sees a real key press, and the
        // dialog sat there ignoring Escape while the test said it worked.
        var closed = 0;
        var close = new RelayCommand(() => closed++);

        Show(() => A_Form(close), dialog =>
        {
            var field = dialog.GetVisualDescendants()
                .OfType<TextBox>()
                .First(t => (t.Name ?? string.Empty) == "FirstField");

            field.Focus();
            AvaloniaUi.Settle();

            // A control that swallows Escape, the way a real one can. With a plain bubbling handler
            // the dialog never sees the key and cannot be closed at all.
            field.AddHandler(InputElement.KeyDownEvent, new EventHandler<KeyEventArgs>((_, e) => e.Handled = true));

            field.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                Source = field,
            });
            AvaloniaUi.Settle();

            Assert.True(closed > 0, "Escape from a field inside the dialog did not close it");
        });
    }

    [Fact]
    public void The_Caret_Lands_Inside_The_Dialog_When_It_Opens()
    {
        // Built hidden and shown afterwards, because that is how every screen in this app does it:
        // the dialogs are declared in the XAML at load and their IsVisible is bound to a flag, so
        // they are attached to the tree long before anyone opens one. A test that builds its dialog
        // already visible agrees with a fix that hooks the wrong moment, which is exactly what the
        // first version of this did: the caret was placed while the dialog was still hidden, where
        // there was nothing to focus, and typing went nowhere.
        AvaloniaUi.Run(window =>
        {
            window.Width = 1200;
            window.Height = 800;

            var dialog = A_Form(new RelayCommand(() => { }));
            dialog.IsVisible = false;

            window.Content = dialog;
            window.Show();
            AvaloniaUi.Settle();

            // Opening it, the way a user does.
            dialog.IsVisible = true;
            AvaloniaUi.Settle();

            // The focus is posted rather than set inline, because at the moment the dialog becomes
            // visible its content control has not realised its child yet. Settle twice runs the
            // posted work. Settle, not Run: asking the UI thread to queue behind itself deadlocks.
            AvaloniaUi.Settle();

            var focused = dialog.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c => c.IsFocused);

            Assert.NotNull(focused);
            Assert.IsAssignableFrom<InputElement>(focused);
        });
    }

    [Fact]
    public void A_Dialog_That_Does_Not_Close_On_The_Backdrop_Still_Swallows_The_Click()
    {
        var close = new RelayCommand(() => { });

        Show(() => A_Form(close), dialog =>
        {
            // Turning this off exists so a stray click cannot lose a half-typed form. Making the
            // backdrop unclickable to achieve that means the click falls through it and lands on
            // the screen behind, which loses the form a different way.
            //
            // So the backdrop stays hit-testable to absorb the click, and only its command goes
            // quiet.
            Assert.True(BackdropOf(dialog).IsHitTestVisible,
                "the backdrop cannot absorb clicks, so they reach the screen underneath");
        });
    }

    [Fact]
    public void The_Dialog_Covers_The_Whole_Screen_Not_Just_The_First_Row()
    {
        // Asserted on the rendered backdrop, because that is what the user sees. A backdrop that
        // stops halfway down the window is not a backdrop.
        AvaloniaUi.Run(window =>
        {
            window.Width = 1200;
            window.Height = 800;

            var host = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

            var filterBar = new Border
            {
                Height = 40,
                Background = Brushes.White,
                Child = new TextBlock { Text = "شريط الفلترة" },
            };
            host.Children.Add(filterBar);

            var content = new Border
            {
                Background = Brushes.LightGray,
                Child = new TextBlock { Text = "الجدول الطويل تحت" },
            };
            Grid.SetRow(content, 1);
            host.Children.Add(content);

            var dialog = A_Form(new RelayCommand(() => { }));
            host.Children.Add(dialog);

            window.Content = host;
            window.Show();
            AvaloniaUi.Settle();

            var bounds = BackdropOf(dialog).Bounds;

            Assert.True(
                bounds.Height > 700,
                $"the backdrop is {bounds.Height:F0}px tall in an 800px window, so the dialog is"
                + " confined to the first row and the screen is clickable through it");
        });
    }

    [Fact]
    public void The_Body_Of_A_Dialog_Is_There_Inside_A_Screen_Laid_Out_Like_PartiesView()
    {
        // The card sizes itself to its content, and the body sits in a star row inside it with a
        // ScrollViewer as the thing whose height is wanted. A ScrollViewer asked how tall it would
        // like to be under an unbounded constraint answers nothing, so the star row collapses: the
        // dialog is left as a title, a subtitle and a footer, and every field in every form in the
        // app is laid out at nothing.
        //
        // It stayed hidden while the card was clipped to the first row of its screen. Fixing the row
        // exposed it.
        //
        // The host below is the real shape: the PartiesView grid, its Auto filter row and its star
        // content row, dialog added as a bare child. A test that puts the dialog straight into a
        // window does not reproduce it, because there the container is bounded and the star row gets
        // handed the slack.
        AvaloniaUi.Run(window =>
        {
            window.Width = 1200;
            window.Height = 800;

            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(new TextBlock { Text = "الاسم *", Classes = { "caption" } });
            body.Children.Add(new TextBox { Name = "FirstField", Height = 38 });
            body.Children.Add(new TextBox { Name = "SecondField", Height = 38 });

            var dialog = new ModalDialog
            {
                Title = "إضافة طرف جديد",
                Subtitle = "أدخل بيانات العميل أو المورد ثم اضغط حفظ",
                CloseCommand = new RelayCommand(() => { }),
                BackdropCloses = true,
                DialogWidth = 520,
            };
            ModalDialog.SetBody(dialog, body);

            var host = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(16),
            };

            host.Children.Add(new Border
            {
                Height = 60,
                Child = new TextBlock { Text = "شريط الفلترة" },
            });

            var list = new Border
            {
                Background = Brushes.LightGray,
                Child = new TextBlock { Text = "قائمة الأطراف" },
            };
            Grid.SetRow(list, 1);
            host.Children.Add(list);

            host.Children.Add(dialog);

            window.Content = host;
            window.Show();
            AvaloniaUi.Settle();

            var field = dialog.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(t => (t.Name ?? string.Empty) == "FirstField");

            Assert.NotNull(field);

            // Not Bounds: a control keeps its arranged size even when an ancestor clips it away, so
            // the first version of this test passed against a dialog that was 0 pixels tall. It has
            // to ask what is actually on screen.
            Assert.True(field!.IsEffectivelyVisible, "the first field is not on screen");

            var card = dialog.GetVisualDescendants()
                .OfType<Border>()
                .First(b => b.Child is Grid rows && rows.RowDefinitions.Count == 3);

            Assert.True(card.Bounds.Height > 100,
                $"the card is {card.Bounds.Height:F0}px tall, so the form inside it is clipped away");

            var fieldTop = field.TranslatePoint(new Point(0, 0), card)!.Value;
            Assert.True(fieldTop.Y > 0 && fieldTop.Y < card.Bounds.Height,
                "the first field is laid out outside the card, which is how a form ends up with a"
                + " title, a footer and nothing to type into");
        });
    }
}
