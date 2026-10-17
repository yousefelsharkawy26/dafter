using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Every dialog in the app is the same shell, written once.
///
/// <para>
/// The overlay, the card, the header with its ✕, and the footer frame were each written out five
/// times across four screens. Nothing caught the copies drifting apart: the card corner radius was
/// 10 in two dialogs and 8 in the others, the ✕ was 34 and then 36, and the widths were 520, 520,
/// 750 and 820. A dialog that looks slightly different from the last one reads as a bug even when
/// it is not.
/// </para>
///
/// <para>
/// The screens hosting this are forms over money, so these check it behaves rather than how it
/// looks: a dialog with no way out of it is not a cosmetic problem.
/// </para>
/// </summary>
public sealed class ModalDialogTests
{
    /// <summary>
    /// Built on the UI thread. Avalonia's service locator is per-thread, and a control built on the
    /// xUnit pool thread cannot resolve a cursor factory.
    /// </summary>
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

    [Fact]
    public void The_X_And_The_Backdrop_Are_The_Same_Close_Command()
    {
        // The two ways out. Neither can be missing, and they cannot disagree: one closing the
        // dialog and the other not is a thing only the user finds.
        Show(() => new ModalDialog { CloseCommand = new RelayCommand(() => { }) }, dialog =>
        {
            var closers = dialog.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Command is not null)
                .ToArray();

            Assert.Equal(2, closers.Length);
            Assert.All(closers, b => Assert.Same(dialog.CloseCommand, b.Command));
        });
    }

    [Fact]
    public void The_Close_Command_Actually_Runs_When_The_X_Is_Pressed()
    {
        var closed = 0;
        Show(() => new ModalDialog { CloseCommand = new RelayCommand(() => closed++) }, dialog =>
        {
            var x = dialog.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => b.Command is not null && b.Bounds.Left > 0);

            x.Command!.Execute(null);

            Assert.Equal(1, closed);
        });
    }

    [Fact]
    public void The_Title_And_Subtitle_Reach_The_Header()
    {
        Show(() => new ModalDialog
             {
                 Title = "إضافة عميل",
                 Subtitle = "أدخل البيانات ثم احفظ",
                 CloseCommand = new RelayCommand(() => { }),
             },
             dialog =>
             {
                 var hints = dialog.GetVisualDescendants()
                     .OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("muted"))
                     .ToArray();

                 // Visible, not merely present: a collapsed line still sits in the visual tree and
                 // still reports its text, so reading Text alone would pass on a dialog whose
                 // subtitle had stopped showing.
                 var hint = Assert.Single(hints);
                 Assert.True(hint.IsVisible);
                 Assert.Equal("أدخل البيانات ثم احفظ", hint.Text);

                 var titles = dialog.GetVisualDescendants()
                     .OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("subtitle"))
                     .ToArray();

                 Assert.Equal("إضافة عميل", Assert.Single(titles).Text);
             });
    }

    [Fact]
    public void A_Title_Held_In_A_Binding_Shows_The_Value_Not_The_Property_Name()
    {
        // This is how the translation from the hand-written dialogs went wrong once: the property
        // name was written as literal text, which compiles and then puts "DialogTitle" in the
        // header instead of the customer's name.
        Show(() => new ModalDialog
             {
                 Title = "عميل جديد",
                 CloseCommand = new RelayCommand(() => { }),
             },
             dialog =>
             {
                 var texts = dialog.GetVisualDescendants().OfType<TextBlock>()
                     .Select(t => t.Text)
                     .Where(t => t is not null)
                     .ToArray();

                 Assert.Contains("عميل جديد", texts);
                 Assert.DoesNotContain(texts, t => t is not null && t.Contains("Title", StringComparison.Ordinal));
             });
    }

    [Fact]
    public void No_Subtitle_Means_No_Empty_Second_Line()
    {
        // Half these dialogs carry a hint and half do not. One without should not leave a blank
        // line where the hint would be.
        Show(() => new ModalDialog { Title = "حذف", CloseCommand = new RelayCommand(() => { }) },
             dialog =>
             {
                 var hints = dialog.GetVisualDescendants()
                     .OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("muted"))
                     .ToArray();

                 // The line is in the template whether or not there is anything to say, so the
                 // check is that it is collapsed, not that it is absent.
                 Assert.Single(hints);
                 Assert.False(hints[0].IsVisible);
             });
    }

    [Fact]
    public void The_Backdrop_Swallows_Clicks_Unless_The_Screen_Opts_In()
    {
        // Losing a half-typed customer to a stray click is worse than reaching for the X, so the
        // default is off even though every screen in the app turns it on.
        //
        // It used to assert that turning the option off made the backdrop unclickable, which is the
        // opposite of swallowing: the click fell through it and landed on the screen behind, on the
        // button the user had just been looking at. What "does not close on a click" has to mean is
        // that the backdrop still takes the click and only its command goes quiet.
        Show(() => new ModalDialog { Title = "عميل", CloseCommand = new RelayCommand(() => { }) },
             dialog =>
             {
                 var backdrop = BackdropOf(dialog);

                 Assert.True(backdrop.IsHitTestVisible,
                    "the backdrop must take the click whatever the setting says");

                 Assert.False(backdrop.IsEnabled);

                 dialog.BackdropCloses = true;
                 AvaloniaUi.Settle();

                 Assert.True(backdrop.IsEnabled);
             });
    }

    [Fact]
    public void The_Card_Is_Opaque_And_Smaller_Than_The_Window()
    {
        // A translucent card means the form behind it shows through the fields the cashier is
        // typing into, which is how prices get read off the wrong row.
        Show(() => new ModalDialog
             {
                 Title = "عميل",
                 CloseCommand = new RelayCommand(() => { }),
                 DialogWidth = 520,
             },
             dialog =>
             {
                 var card = dialog.GetVisualDescendants()
                     .OfType<Border>()
                     .First(b => b.BoxShadow.Count > 0);

                 var fill = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
                 Assert.Equal(255, (int)fill.Color.A);
                 Assert.Equal(520, card.Bounds.Width);
             });
    }

    [Fact]
    public void The_Tinted_Header_Stays_Legible()
    {
        // The settling wizard's header is white on the app's blue. A ✕ that has lost its foreground
        // is the one thing in a dialog that cannot be read at all, and it was written inline in
        // each of the two dialogs that wanted a blue header.
        Show(() => new ModalDialog
             {
                 Title = "تسوية جولة",
                 CloseCommand = new RelayCommand(() => { }),
                 PrimaryHeader = true,
             },
             dialog =>
             {
                 var header = dialog.GetVisualDescendants()
                     .OfType<Border>()
                     .Single(b => b.Classes.Contains("header"));

                 Assert.Equal(
                     Color.Parse("#0078D7"),
                     Assert.IsAssignableFrom<ISolidColorBrush>(header.Background).Color);

                 var close = dialog.GetVisualDescendants()
                     .OfType<Button>()
                     .Single(b => b.Classes.Contains("header-close"));

                 var glyph = close.GetVisualDescendants().OfType<TextBlock>().Single();
                 Assert.Equal(
                     Colors.White,
                     Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground!).Color);
             });
    }

    [Fact]
    public void A_Plain_Header_Keeps_The_Neutral_Fill()
    {
        Show(() => new ModalDialog { Title = "عميل", CloseCommand = new RelayCommand(() => { }) },
             dialog =>
             {
                 var header = dialog.GetVisualDescendants()
                     .OfType<Border>()
                     .Single(b => b.Classes.Contains("header"));

                 Assert.NotEqual(
                     Color.Parse("#0078D7"),
                     Assert.IsAssignableFrom<ISolidColorBrush>(header.Background).Color);
             });
    }

    [Fact]
    public void A_Fixed_Height_Reaches_The_Card()
    {
        // A wizard whose frame resizes as its steps change throws the user off. This is the knob
        // that stops it, and it has to actually reach the card rather than sitting on the property.
        Show(() => new ModalDialog
             {
                 Title = "معالج",
                 CloseCommand = new RelayCommand(() => { }),
                 DialogHeight = 650,
             },
             dialog =>
             {
                 var card = dialog.GetVisualDescendants()
                     .OfType<Border>()
                     .First(b => b.BoxShadow.Count > 0);

                 Assert.Equal(650, card.Bounds.Height);
             });
    }

    [Fact]
    public void Header_Extra_Sits_Beside_The_Title_Not_In_Place_Of_It()
    {
        // The payments dialog switches between three forms and the switcher sits next to the
        // heading. Putting it where the title goes hides the title, which is how this first went in.
        // So this reads what is on screen, not which slot the markup put it in.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1200;
            window.Height = 800;

            var dialog = new ModalDialog
            {
                Title = "سند",
                Subtitle = "أدخل التفاصيل",
                CloseCommand = new RelayCommand(() => { }),
            };
            ModalDialog.SetHeaderExtra(dialog, new TextBlock { Text = "tabs" });

            window.Content = dialog;
            window.Show();
            AvaloniaUi.Settle();

            var shown = dialog.GetVisualDescendants()
                .OfType<TextBlock>()
                // IsEffectivelyVisible, not IsVisible: IsVisible is the local setting, and a
                // TextBlock inside a collapsed panel still reports true. That is why the first
                // attempt at this test passed against markup that hid the title.
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text)
                .ToArray();

            Assert.Contains("سند", shown);
            Assert.Contains("tabs", shown);
        });
    }

    [Fact]
    public void Without_Header_Extra_The_Slot_Leaves_Nothing_Behind()
    {
        // A header slot that reserves its space even with nothing in it pushes the ✕ away from the
        // edge of the card for no reason.
        Show(() => new ModalDialog { Title = "سند", CloseCommand = new RelayCommand(() => { }) },
             dialog =>
             {
                 var close = dialog.GetVisualDescendants()
                     .OfType<Button>()
                     .Single(b => b.Classes.Contains("header-close"));

                 Assert.True(close.IsVisible);
                 Assert.Contains("سند", dialog.GetVisualDescendants()
                     .OfType<TextBlock>().Select(t => t.Text));
             });
    }
}