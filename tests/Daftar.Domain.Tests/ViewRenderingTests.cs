using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Daftar.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Renders the real views against no display, and writes a PNG of each.
///
/// Everything else in this suite tests view models and services. That leaves what only exists at
/// runtime untested: an imperative column toggle, a template that fails to apply, a control that
/// throws during layout. The build validates bind paths because Avalonia compiles them, but it
/// cannot lay anything out.
///
/// The screenshots are the point as much as the assertions. A screen can pass every check and
/// still be unusable, and that is not something a test can tell you -- somebody has to look.
/// </summary>
public sealed class ViewRenderingTests
{
    public ViewRenderingTests() => AvaloniaUi.EnsureStarted();

    // ---------- the reports screen ----------

    /// <summary>
    /// ReportsView drops the cost, margin and profit columns from the grids in C# when
    /// <c>CanViewCost</c> goes false. No bind-path check can catch that -- it runs after the view
    /// is attached, in code the XAML compiler never sees. This is the only thing that exercises it.
    /// </summary>
    [Fact]
    public void The_Cost_Columns_Disappear_Without_The_Permission()
    {
        AvaloniaUi.Run(window =>
        {
            var view = new Views.ReportsView
            {
                DataContext = new ViewModels.ReportsViewModel(Container()) { CanViewCost = false },
            };

            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            var headers = GridHeaders(view);

            Assert.DoesNotContain("التكلفة", headers);
            Assert.DoesNotContain("صافي الربح", headers);
            Assert.DoesNotContain("نسبة الربح", headers);
            Assert.DoesNotContain("إجمالي التكلفة", headers);

            // Revenue is still there. Hiding the numbers an owner keeps must not hide the ones
            // everyone is allowed to see.
            Assert.Contains("إجمالي الفاتورة", headers);
            Assert.Contains("إجمالي المبيعات", headers);
        });
    }

    [Fact]
    public void The_Cost_Columns_Come_Back_When_The_Permission_Returns()
    {
        // A role can be edited while the window is open, so the columns have to be restorable
        // rather than merely removable. Losing them on the way back would silently turn the
        // owner's reports into a cashier's and never say so.
        //
        // The flag is flipped after the view is attached rather than before: ReportsView loads
        // its view model on attach, and that load recomputes CanViewCost from the signed-in
        // role. A value set beforehand is overwritten before the view ever sees it.
        AvaloniaUi.Run(window =>
        {
            var vm = new ViewModels.ReportsViewModel(Container());

            var view = new Views.ReportsView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            Assert.DoesNotContain("التكلفة", GridHeaders(view));

            vm.CanViewCost = true;
            AvaloniaUi.Settle();

            var headers = GridHeaders(view);
            Assert.Contains("التكلفة", headers);
            Assert.Contains("صافي الربح", headers);
            Assert.Contains("نسبة الربح", headers);
        });
    }

    // ---------- the screens render ----------

    [Fact]
    public void The_Reports_Screen_Renders()
    {
        AvaloniaUi.Run(window =>
        {
            window.Content = new Views.ReportsView
            {
                DataContext = new ViewModels.ReportsViewModel(Container()) { CanViewCost = true },
            };

            Capture(window, "reports");
        });
    }

    /// <summary>
    /// The five headline cards are one row of one size.
    ///
    /// Two of the five are hand-written rather than built from the tile, so they do not inherit the
    /// tile's figure size. When the tile's figure was resized to clear the contrast floor they were
    /// left behind at the old size, and the row showed its second and third numbers a step smaller
    /// than its first, fourth and fifth. No view model can see that and no bind path is wrong, so
    /// it is caught here or it is not caught.
    ///
    /// A figure in this row is identified by being bold: the labels are muted, the icons are not
    /// bold, and the badge in the margin card's note is only semi-bold. The floor asserted is the
    /// WCAG large-text line, because that is the reason the size is what it is -- at 18.66 DIP a
    /// colour stops owing 4.5 and starts owing 3.0.
    /// </summary>
    [Fact]
    public void Every_Figure_In_The_Reports_Kpi_Strip_Is_One_Size()
    {
        AvaloniaUi.Run(window =>
        {
            var vm = new ViewModels.ReportsViewModel(Container());

            var view = new Views.ReportsView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            // Granted after the view is attached, because ReportsView recomputes the permission
            // from the signed-in role while it loads: a value set beforehand is overwritten and the
            // two right-hand cards -- the ones this test is about -- never appear at all.
            vm.CanViewCost = true;
            AvaloniaUi.Settle();

            Capture(window, "reports-cost");

            var strip = view.GetVisualDescendants().OfType<UniformGrid>().Single();

            var figures = strip.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(figure => figure.FontWeight >= FontWeight.Bold)
                .ToList();

            Assert.Equal(5, figures.Count);

            Assert.All(figures, figure =>
                Assert.True(figure.IsEffectivelyVisible, $"\"{figure.Text}\" is in the strip but not shown"));

            var size = Assert.Single(figures.Select(figure => figure.FontSize).Distinct());

            Assert.True(size >= 18.66,
                $"the strip figures are {size} DIP, which is not large text and owes a 4.5 floor");
        });
    }

    [Fact]
    public void The_Settings_Screen_Renders()
    {
        AvaloniaUi.Run(window =>
        {
            window.Content = new Views.SettingsView { DataContext = new ViewModels.SettingsViewModel(Container()) };

            Capture(window, "settings");
        });
    }

    [Fact]
    public void The_Login_Screen_Renders_No_Boolean_As_Text()
    {
        // The password toggle was written as
        //   Text="{Binding RevealPassword, Converter={x:Null}, FallbackValue='👁️'}"
        // which reads like it shows an eye and actually shows the word "False". A null converter
        // is not a conversion: Avalonia falls back to ToString, and FallbackValue only rescues a
        // binding that fails outright rather than one that converts badly.
        //
        // Walking the rendered tree is the only way to catch this class of bug. The build is
        // happy -- the path is a real property -- and every view model test is happy.
        // LoginWindow is already a Window, so it is shown directly rather than hosted in the
        // scratch window the harness provides.
        AvaloniaUi.Run(_ =>
        {
            var view = new Views.LoginWindow { DataContext = new ViewModels.LoginViewModel(Container()) };
            view.Show();
            AvaloniaUi.Settle();

            Capture(view, "login");

            var offenders = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => t is not null
                            && (t.Equals("True", StringComparison.OrdinalIgnoreCase)
                                || t.Equals("False", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.Empty(offenders);
        });
    }

    /// <summary>
    /// The start screen's low-stock tile prints its number in a size of its own and the word beside
    /// it smaller, in two text blocks. Only the number was ever bound; the word was the literal
    /// "أصناف", so at one product the tile read "1 أصناف" and could not move with the count at all.
    ///
    /// <para>
    /// This is the shape a counted-noun sweep misses: the word is not inside a StringFormat, it is a
    /// text block of its own. The count set here is two because that is the only count whose form
    /// differs from zero and one as well as from three and up -- a word that never moved would still
    /// have read correctly at any other value.
    /// </para>
    /// </summary>
    [Fact]
    public void The_Start_Screen_Low_Stock_Figure_And_Its_Noun_Agree()
    {
        var rendered = new List<string>();

        AvaloniaUi.Run(_ =>
        {
            var vm = new ViewModels.MainWindowViewModel { Services = Container() };

            var window = new Views.MainWindow { DataContext = vm };
            window.Show();
            AvaloniaUi.Settle();
            AvaloniaUi.Settle();

            // Set after the start screen has loaded: the load writes this figure, and a value set
            // beforehand would be settled back to the one it read.
            vm.Stats!.LowStockCount = 2;
            AvaloniaUi.Settle();

            Capture(window, "start-menu-lowstock");

            // The tile by its label, so the assertion is about this tile and not about any other
            // number the start screen happens to show.
            var tile = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text == "تنبيهات النواقص"));

            rendered.AddRange(tile.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty));
        });

        Assert.Contains("2", rendered);
        Assert.Contains("صنفان", rendered);
    }

    // ---------- helpers ----------

    /// <summary>
    /// A container with no database behind it. Nothing here loads: the views under test take their
    /// state from the view model directly, and standing one up with a real ledger would only
    /// prove the same thing the service tests already do.
    /// </summary>
    private static IServiceProvider Container()
        => new ServiceCollection()
            .AddDaftarPersistence(Path.Combine(Path.GetTempPath(), "daftar-render-unused.db"))
            .BuildServiceProvider();

    private static List<string> GridHeaders(Views.ReportsView view)
    {
        var headers = new List<string>();

        // By name, for the same reason ReportsView does it: the generated fields are null until
        // the view is attached, and reading them from a test would silently report no columns at
        // all -- a passing assertion about nothing.
        foreach (var grid in new[]
                 {
                     view.FindControl<DataGrid>("SalesGrid"),
                     view.FindControl<DataGrid>("ProductsGrid"),
                 })
        {
            if (grid is null)
                continue;

            foreach (var column in grid.Columns)
                if (column.Header is string header)
                    headers.Add(header);
        }

        return headers;
    }

    private static string ShotDirectory { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "screenshots"));

    private static void Capture(Window window, string name)
    {
        window.Show();
        AvaloniaUi.Settle();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        Directory.CreateDirectory(ShotDirectory);
        using (frame)
        {
            frame.Save(Path.Combine(ShotDirectory, $"{name}.png"), new PngBitmapEncoderOptions());
        }

        Assert.True(window.Bounds.Width > 0);
    }
}
