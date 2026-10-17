using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// One search field for the whole app.
///
/// <para>
/// It was written out eight times across six screens in two sizes, and the sizes had drifted
/// further than that: the till's field used the theme's search height and a 16pt icon while the
/// reports filters hard-coded 38 and a 15pt icon.
/// </para>
///
/// <para>
/// Two of the eight are the till's, and those are the interesting ones. They carry a
/// <c>KeyDown</c> handler that runs the barcode search and a name that the screen looks up to put
/// the caret in the field. Wrapping a field in a control is exactly the kind of change that
/// breaks both of those silently, so they are pinned here.
/// </para>
/// </summary>
public sealed class SearchBoxTests
{
    /// <summary>
    /// Built on the UI thread: Avalonia's service locator is per-thread, and a control built on the
    /// xUnit pool thread cannot resolve a cursor factory.
    /// </summary>
    private static void Show(System.Func<SearchBox> build, System.Action<SearchBox> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var box = build();
            window.Content = box;
            window.Show();
            AvaloniaUi.Settle();

            check(box);
        });
    }

    private static TextBox Field(SearchBox box) =>
        box.GetVisualDescendants().OfType<TextBox>().Single();

    [Fact]
    public void The_Magnifier_Is_Inside_The_Field_Not_Beside_It()
    {
        // An icon inside a TextBox is content, not a property, which is the whole reason this is a
        // control: no style could reach it.
        Show(() => new SearchBox { Placeholder = "ابحث" }, box =>
        {
            var icon = box.GetVisualDescendants().OfType<PathIcon>().Single();

            Assert.True(icon.IsVisible);
            Assert.True(Field(box).Bounds.Height > 0);
        });
    }

    [Fact]
    public void The_Large_Form_Is_The_Taller_Of_The_Two()
    {
        // The till's field is the one a barcode scanner types into. If it shrinks to the size of a
        // reports filter the cashier stops hitting it.
        Show(() => new SearchBox { Large = true }, box => Assert.True(Field(box).Bounds.Height >= 44));

        Show(() => new SearchBox(), box => Assert.True(Field(box).Bounds.Height < 44));
    }

    [Fact]
    public void Typing_Into_The_Field_Writes_Back_To_The_Bound_Text()
    {
        // The binding is one way in only if the field is written from outside, which is what a
        // scanned barcode does. Both directions have to work.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var box = new SearchBox { Text = "" };
            window.Content = box;
            window.Show();
            AvaloniaUi.Settle();

            Field(box).Text = "SKU-1";
            AvaloniaUi.Settle();

            Assert.Equal("SKU-1", box.Text);
        });
    }

    [Fact]
    public void A_Text_Set_From_Outside_Reaches_The_Field()
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var box = new SearchBox();
            window.Content = box;
            window.Show();
            AvaloniaUi.Settle();

            box.Text = "امسح الباركود";
            AvaloniaUi.Settle();

            Assert.Equal("امسح الباركود", Field(box).Text);
        });
    }

    [Fact]
    public void FocusSearch_Puts_The_Caret_In_The_Field()
    {
        // The till focuses this field on every screen change. Before it was a component it was a
        // TextBox the screen found by name; wrapping it broke that lookup, and a search box the
        // cashier cannot put the caret in is one they have to click.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var box = new SearchBox();
            window.Content = box;
            window.Show();
            AvaloniaUi.Settle();

            box.FocusSearch();
            AvaloniaUi.Settle();

            Assert.True(Field(box).IsFocused);
        });
    }

    [Fact]
    public void Pressing_Enter_Still_Reaches_A_Handler_On_The_Screen()
    {
        // KeyDown is routed, and the till's handler sits on the screen, not on the field. Wrapping
        // the field in a UserControl must not stop the event on the way up: the barcode that gets
        // typed rather than scanned goes through here.
        AvaloniaUi.EnsureStarted();

        var presses = 0;

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var box = new SearchBox();
            // Stands in for SellView.OnSearchKeyDown, which the sell and receive screens attach to
            // the element that used to be the TextBox itself.
            box.AddHandler(Control.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter) presses++;
            });

            window.Content = box;
            window.Show();
            AvaloniaUi.Settle();

            var field = Field(box);
            field.Focus();
            AvaloniaUi.Settle();

            field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            AvaloniaUi.Settle();
        });

        Assert.Equal(1, presses);
    }
}