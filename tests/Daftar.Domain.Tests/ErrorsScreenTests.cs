using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Daftar.Desktop.Diagnostics;
using Daftar.ViewModels;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The errors screen, from the entry to what is drawn.
///
/// <para>
/// What is not covered here is the single line where the view model asks the reader for the app's
/// log directory. Pointing that at a temporary directory would have meant either a test-only
/// parameter in the view model or writing a file into the operator's real app data; neither is
/// worth it for one line. The work either side of it is tested — <see cref="LogReaderTests"/>
/// covers reading files, and these cover everything the screen does with what comes back.
/// </para>
/// </summary>
public sealed class ErrorsScreenTests
{
    private static LogEntry Fault(string message, string detail = "at somewhere") =>
        new(new DateTimeOffset(2026, 10, 11, 14, 23, 45, TimeSpan.Zero),
            "ERROR", "Daftar", message, detail);

    // ---------- the view model ----------

    [Fact]
    public void A_Quiet_App_Says_Plainly_That_There_Is_Nothing()
    {
        var vm = new ErrorsViewModel();

        Assert.False(vm.HasEntries);
        Assert.False(vm.HasSelection);
        Assert.Equal("مفيش أخطاء مسجّلة.", vm.SummaryText);
    }

    [Fact]
    public void The_Summary_Names_The_Count_And_The_Most_Recent()
    {
        // The screen is opened because something went wrong, so "how bad, and how recent" has to
        // be readable without scrolling a list.
        var vm = new ErrorsViewModel();
        vm.Entries.Add(Fault("newest"));
        vm.Entries.Add(Fault("older"));

        Assert.True(vm.HasEntries);
        Assert.Contains("2 خطأ", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("11/10 14:23:45", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_A_Fault_Announces_It_Rather_Than_Waiting_To_Be_Asked()
    {
        // The headline is computed off the collection, so a screen bound to it only learns about
        // a new fault when something says so. Reading the property after the fact would pass
        // either way, which is why this watches what was announced.
        var vm = new ErrorsViewModel();
        var announced = new List<string?>();
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        vm.Entries.Add(Fault("a fault"));

        Assert.Contains(nameof(ErrorsViewModel.HasEntries), announced);
        Assert.Contains(nameof(ErrorsViewModel.SummaryText), announced);
    }

    [Fact]
    public void Selecting_A_Fault_Reveals_Its_Detail()
    {
        // The stack trace is what names the cause, and the list column truncates it.
        var vm = new ErrorsViewModel();
        var fault = Fault("Post sale", "System.InvalidOperationException: database is locked");

        Assert.False(vm.HasSelection);

        vm.Entries.Add(fault);
        vm.Selected = fault;

        Assert.True(vm.HasSelection);
        Assert.NotNull(vm.Selected);
        Assert.Equal(fault.Detail, vm.Selected.Detail);
    }

    [Fact]
    public void Clearing_The_Selection_Hides_The_Detail()
    {
        var vm = new ErrorsViewModel { Selected = Fault("Post sale") };

        vm.Selected = null;

        Assert.False(vm.HasSelection);
    }

    [Fact]
    public void The_Screen_Tells_The_Operator_Where_The_Files_Are()
    {
        // So the answer can be handed to whoever is looking without guessing at a path.
        Assert.Contains("logs", new ErrorsViewModel().LogDirectoryText, StringComparison.Ordinal);
    }

    // ---------- the drawing ----------

    // The view fills itself from the real log directory as soon as it is given a view model,
    // which is right for the app and useless for a test: the machine's own log decides what the
    // screen contains. So each of these opens the view, then sets the contents itself and checks
    // that what is drawn follows. What is under test is the binding, not the file system.

    [Fact]
    public void With_Nothing_To_Show_The_Screen_Says_So_Instead_Of_Drawing_An_Empty_Table()
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            var vm = new ErrorsViewModel();
            var view = new Views.ErrorsView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            vm.Entries.Clear();
            AvaloniaUi.Settle();

            Assert.False(view.FindControl<Border>("ErrorsListPanel")!.IsVisible);
            Assert.True(view.FindControl<Border>("ErrorsEmptyPanel")!.IsVisible);
            Assert.Contains("مفيش أخطاء مسجّلة.", TextIn(view), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_Fault_Shows_Its_Time_Level_And_Place()
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            var vm = new ErrorsViewModel();
            var view = new Views.ErrorsView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            vm.Entries.Clear();
            vm.Entries.Add(Fault("Post sale", "database is locked"));
            AvaloniaUi.Settle();

            Assert.True(view.FindControl<Border>("ErrorsListPanel")!.IsVisible);
            Assert.False(view.FindControl<Border>("ErrorsEmptyPanel")!.IsVisible);

            var headers = view.FindControl<DataGrid>("ErrorsGrid")!.Columns
                .OfType<DataGridTextColumn>()
                .Select(c => c.Header?.ToString() ?? string.Empty)
                .ToArray();

            Assert.Equal(new[] { "الوقت", "النوع", "المكان", "الرسالة" }, headers);
        });
    }

    [Fact]
    public void The_Detail_Pane_Only_Appears_Once_Something_Is_Selected()
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            var vm = new ErrorsViewModel();
            var view = new Views.ErrorsView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            var fault = Fault("Post sale", "System.InvalidOperationException: database is locked");
            vm.Entries.Clear();
            vm.Entries.Add(fault);
            AvaloniaUi.Settle();

            Assert.False(view.FindControl<Border>("ErrorsDetailPanel")!.IsVisible);

            vm.Selected = fault;
            AvaloniaUi.Settle();

            Assert.True(view.FindControl<Border>("ErrorsDetailPanel")!.IsVisible);

            // The cause itself, in full. The list truncates it, and this is the whole reason the
            // screen exists.
            Assert.Contains("database is locked", TextIn(view), StringComparison.Ordinal);
        });
    }

    private static string TextIn(Control root) =>
        string.Join('\n', root.GetVisualDescendants()
            .Select(n => n switch
            {
                TextBlock t => t.Text,
                // The detail lives in a read-only box so it can be selected and copied; its text
                // is drawn from a template, so reading TextBlocks alone would miss the one line
                // on this screen that matters.
                TextBox b => b.Text,
                _ => null,
            })
            .Where(t => t is not null));
}