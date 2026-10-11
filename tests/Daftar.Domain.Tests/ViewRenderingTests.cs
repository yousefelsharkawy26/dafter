using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
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

    [Fact]
    public void The_Settings_Screen_Renders()
    {
        AvaloniaUi.Run(window =>
        {
            window.Content = new Views.SettingsView { DataContext = new ViewModels.SettingsViewModel(Container()) };

            Capture(window, "settings");
        });
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
