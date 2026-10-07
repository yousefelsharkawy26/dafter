using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Views;

namespace Daftar.ViewModels;

/// <summary>
/// Owns the open tabs. The launcher strip shows a button per page; choosing one opens a tab, or
/// focuses it if it is already open — the behaviour of a browser.
///
/// Both the view and view model types are declared here, so a typo is a compile error rather than
/// a page that silently renders empty.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly Dictionary<string, TabSpec> _pages;

    public MainWindowViewModel()
    {
        Tabs = [];

        _pages = new Dictionary<string, TabSpec>
        {
            [TabKeys.Dashboard] = new(TabKeys.Dashboard, TabKeys.TitleFor(TabKeys.Dashboard),
                typeof(DashboardView), typeof(DashboardViewModel),
                "نظرة عامة على اليوم", CanClose: false),

            [TabKeys.Sell] = new(TabKeys.Sell, TabKeys.TitleFor(TabKeys.Sell),
                typeof(SellView), typeof(SellViewModel), "فاتورة بيع"),

            [TabKeys.Products] = new(TabKeys.Products, TabKeys.TitleFor(TabKeys.Products),
                typeof(ProductsView), typeof(ProductsViewModel), "إضافة وتعديل الأصناف ووحداتها وأسعارها"),

            [TabKeys.Receive] = new(TabKeys.Receive, TabKeys.TitleFor(TabKeys.Receive),
                typeof(ReceiveView), typeof(ReceiveViewModel), "استلام فاتورة مورد وتوليد اللوتات"),

            // No screen yet: these render a placeholder that says what the page will do.
            [TabKeys.Returns] = Placeholder(TabKeys.Returns, "مرتجع العميل وتالف المرتجع"),
            [TabKeys.Payments] = new(TabKeys.Payments, TabKeys.TitleFor(TabKeys.Payments),
                typeof(PaymentsView), typeof(PaymentsViewModel),
                "تحصيل من العملاء ودفع للموردين"),
            [TabKeys.Stock] = Placeholder(TabKeys.Stock, "المخزون الحالي وقرب الانتهاء والتحويلات"),
            [TabKeys.Parties] = Placeholder(TabKeys.Parties, "العملاء والموردون والأرصدة"),
            [TabKeys.Distributors] = Placeholder(TabKeys.Distributors, "الموزعون ومركباتهم وحساباتهم"),
            [TabKeys.Sessions] = Placeholder(TabKeys.Sessions, "فتح جولة الموزع وتسويتها"),
            [TabKeys.Reports] = Placeholder(TabKeys.Reports, "الأرباح والتحصيلات والمتأخرات"),
            [TabKeys.Settings] = Placeholder(TabKeys.Settings, "بيانات الشركة والفواتير"),
        };

        Launcher = TabKeys.Order
            .Where(_pages.ContainsKey)
            .Select(key => new LauncherButton(key, TabKeys.TitleFor(key)))
            .ToList();

        Tabs.Add(new TabViewModel(_pages[TabKeys.Dashboard]));
        SelectedTab = Tabs[0];
    }

    private static TabSpec Placeholder(string key, string caption)
        => new(key, TabKeys.TitleFor(key), null, null, caption);

    public ObservableCollection<TabViewModel> Tabs { get; }

    public IReadOnlyList<LauncherButton> Launcher { get; }

    [ObservableProperty]
    private TabViewModel? _selectedTab;

    [ObservableProperty]
    private string _statusText = "جاهز";

    [ObservableProperty]
    private string _currentUserName = string.Empty;

    [ObservableProperty]
    private bool _isStartMenuOpen;

    [RelayCommand]
    public void ToggleStartMenu() => IsStartMenuOpen = !IsStartMenuOpen;

    [RelayCommand]
    public void CloseStartMenu() => IsStartMenuOpen = false;

    /// <summary>Handed to TabHost so each page can resolve its own view model.</summary>
    public IServiceProvider? Services { get; init; }

    public sealed record LauncherButton(string Key, string Title);

    /// <summary>Opens a page, or focuses it when it is already open.</summary>
    [RelayCommand]
    public void Open(string? key)
    {
        IsStartMenuOpen = false;

        if (key is null || !_pages.TryGetValue(key, out var spec))
            return;

        var existing = Tabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new TabViewModel(spec);
        Tabs.Add(tab);
        SelectedTab = tab;
        StatusText = tab.Title;
    }

    [RelayCommand]
    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null || !tab.CanClose)
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    /// <summary>Closes every page except the home tab and whatever is selected.</summary>
    [RelayCommand]
    public void CloseOtherTabs()
    {
        if (SelectedTab is null)
            return;

        var keep = SelectedTab.Key;
        foreach (var tab in Tabs.Where(t => t.Key != keep && t.CanClose).ToList())
            Tabs.Remove(tab);

        StatusText = "تم إغلاق باقي التبويبات";
    }

    [RelayCommand]
    public void FocusNextTab() => FocusOffset(1);

    [RelayCommand]
    public void FocusPrevTab() => FocusOffset(-1);

    public void FocusOffset(int offset)
    {
        if (Tabs.Count == 0 || SelectedTab is null)
            return;

        var index = (Tabs.IndexOf(SelectedTab) + offset + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    /// <summary>Called by a page to raise a notice in the status bar.</summary>
    public void Notify(string message) => StatusText = message;
}