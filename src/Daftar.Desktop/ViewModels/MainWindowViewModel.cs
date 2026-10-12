using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Views;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly Daftar.Services.INavigationService? _navigation;
    private IServiceProvider? _services;

    public MainWindowViewModel() : this(null) { }

    public MainWindowViewModel(Daftar.Services.INavigationService? navigation)
    {
        _navigation = navigation;
        if (_navigation is not null)
        {
            _navigation.Navigated += Open;
        }

        Tabs = [];
        _services = App.Services;

        _pages = new Dictionary<string, TabSpec>
        {
            [TabKeys.Sell] = new(TabKeys.Sell, TabKeys.TitleFor(TabKeys.Sell),
                typeof(SellView), typeof(SellViewModel), "فاتورة بيع"),

            [TabKeys.Products] = new(TabKeys.Products, TabKeys.TitleFor(TabKeys.Products),
                typeof(ProductsView), typeof(ProductsViewModel), "إضافة وتعديل الأصناف ووحداتها وأسعارها"),

            [TabKeys.Receive] = new(TabKeys.Receive, TabKeys.TitleFor(TabKeys.Receive),
                typeof(ReceiveView), typeof(ReceiveViewModel), "استلام فاتورة مورد وتوليد اللوتات"),

            [TabKeys.Returns] = new(TabKeys.Returns, TabKeys.TitleFor(TabKeys.Returns),
                typeof(ReturnsView), typeof(ReturnsViewModel),
                "مرتجع العميل وتالف المرتجع"),

            [TabKeys.Payments] = new(TabKeys.Payments, TabKeys.TitleFor(TabKeys.Payments),
                typeof(PaymentsView), typeof(PaymentsViewModel),
                "تحصيل من العملاء ودفع للموردين"),

            [TabKeys.Stock] = new(TabKeys.Stock, TabKeys.TitleFor(TabKeys.Stock),
                typeof(StockView), typeof(StockViewModel),
                "المخزون الحالي وقرب الانتهاء"),

            [TabKeys.Parties] = new(TabKeys.Parties, TabKeys.TitleFor(TabKeys.Parties),
                typeof(PartiesView), typeof(PartiesViewModel),
                "العملاء والموردون والأرصدة"),

            [TabKeys.Sessions] = new(TabKeys.Sessions, TabKeys.TitleFor(TabKeys.Sessions),
                typeof(SessionsView), typeof(SessionsViewModel),
                "الموزعون ومركباتهم وحساباتهم"),

            [TabKeys.Reports] = new(TabKeys.Reports, TabKeys.TitleFor(TabKeys.Reports),
                typeof(ReportsView), typeof(ReportsViewModel),
                "الأرباح والتحصيلات والمتأخرات"),

            [TabKeys.Settings] = new(TabKeys.Settings, TabKeys.TitleFor(TabKeys.Settings),
                typeof(SettingsView), typeof(SettingsViewModel),
                "بيانات الشركة والفواتير"),
        };

        Launcher = TabKeys.Order
            .Where(_pages.ContainsKey)
            .Select(key => new LauncherButton(key, TabKeys.TitleFor(key)))
            .ToList();

        // Start Menu is the default landing screen (no Dashboard tab).
        _isStartMenuOpen = true;
    }

    /// <summary>
    /// The permission a page needs before it is offered. Null means everyone may open it.
    /// </summary>
    /// <remarks>
    /// This hides the page, it does not protect it: the enforcement that matters is in
    /// SaveChanges and the posting services. Hiding it means a cashier is not offered a screen
    /// whose every action would be refused, which is a worse experience than a visible screen that
    /// explains the limit.
    /// </remarks>
    private static readonly Dictionary<string, string> PagePermissions = new(StringComparer.Ordinal)
    {
        [TabKeys.Settings] = Domain.Security.Role.Codes.UsersManage,
        [TabKeys.Products] = Domain.Security.Role.Codes.PriceOverride,
    };

    /// <summary>
    /// Whether the signed-in role may open a page. Falls back to allowing it when the identity
    /// cannot be resolved, so a permission problem never locks the operator out of the till.
    /// </summary>
    private async Task<bool> CanOpenPageAsync(string key)
    {
        if (!PagePermissions.TryGetValue(key, out var code))
            return true;

        if (_services is null)
            return true;

        using var scope = _services.CreateScope();
        var authorizer = scope.ServiceProvider.GetService<Daftar.Infrastructure.IUserAuthorizer>();
        if (authorizer is null)
            return true;

        return await authorizer.CanAsync(code);
    }

    /// <summary>Applies the role filter to the launcher strip.</summary>
    public async Task ApplyPermissionsAsync()
    {
        var allowed = new List<string>();

        foreach (var key in TabKeys.Order.Where(_pages.ContainsKey))
            if (await CanOpenPageAsync(key))
                allowed.Add(key);

        Launcher = allowed.Select(key => new LauncherButton(key, TabKeys.TitleFor(key))).ToList();
        OnPropertyChanged(nameof(Launcher));
    }

    private static TabSpec Placeholder(string key, string caption)
        => new(key, TabKeys.TitleFor(key), null, null, caption);

    public ObservableCollection<TabViewModel> Tabs { get; }

    public IReadOnlyList<LauncherButton> Launcher { get; private set; }

    public bool HasOpenTabs => Tabs.Count > 0;

    [ObservableProperty]
    private TabViewModel? _selectedTab;

    [ObservableProperty]
    private string _statusText = "جاهز";

    [ObservableProperty]
    private string _currentUserName = string.Empty;

    [ObservableProperty]
    private bool _isStartMenuOpen = true;

    [ObservableProperty]
    private DashboardViewModel? _stats;

    public event Action? RequestSignOut;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    public string CurrentDateText => DateTime.Now.ToString("dddd، d MMMM yyyy", new System.Globalization.CultureInfo("ar-EG"));

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var period = (hour >= 4 && hour < 12) ? "صباح الخير" : "مساء الخير";
            return string.IsNullOrEmpty(CurrentUserName) ? period : $"{period}، {CurrentUserName}";
        }
    }

    [RelayCommand]
    public void SignOut() => RequestSignOut?.Invoke();

    [RelayCommand]
    public async Task RefreshStatsAsync()
    {
        if (Stats is not null)
        {
            await Stats.LoadAsync();
            StatusText = $"تم تحديث البيانات — {DateTime.Now:HH:mm:ss}";
        }
    }

    [RelayCommand]
    public void ToggleStartMenu()
    {
        IsStartMenuOpen = !IsStartMenuOpen;
        if (IsStartMenuOpen)
        {
            _ = Stats?.LoadAsync();
        }
    }

    [RelayCommand]
    public void CloseStartMenu()
    {
        if (HasOpenTabs)
            IsStartMenuOpen = false;
    }

    /// <summary>Handed to TabHost so each page can resolve its own view model.</summary>
    public IServiceProvider? Services
    {
        get => _services;
        init
        {
            _services = value;
            if (value is not null)
            {
                Stats = new DashboardViewModel(value);
                _ = Stats.LoadAsync();
            }
            if (_navigation is null && value?.GetService(typeof(Daftar.Services.INavigationService)) is Daftar.Services.INavigationService nav)
            {
                nav.Navigated += Open;
            }
        }
    }

    public sealed record LauncherButton(string Key, string Title);

    /// <summary>Opens a page, or focuses it when it is already open.</summary>
    [RelayCommand]
    public void Open(string? key)
    {
        if (key is null || !_pages.TryGetValue(key, out var spec))
            return;

        // The launcher already omits pages the role cannot open, so reaching this with a hidden key
        // means a keyboard shortcut or a stale tab. Refuse rather than render a screen whose every
        // action would be rejected at save time.
        if (!Launcher.Any(b => b.Key == key))
        {
            StatusText = "ماعندكش صلاحية تفتح الصفحة دي.";
            return;
        }

        IsStartMenuOpen = false;

        var existing = Tabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            SelectedTab = existing;
            StatusText = existing.Title;
            OnPropertyChanged(nameof(HasOpenTabs));
            return;
        }

        var tab = new TabViewModel(spec);
        Tabs.Add(tab);
        SelectedTab = tab;
        StatusText = tab.Title;
        OnPropertyChanged(nameof(HasOpenTabs));
    }

    [RelayCommand]
    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null || !tab.CanClose)
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            SelectedTab = null;
            IsStartMenuOpen = true;
            _ = Stats?.LoadAsync();
        }
        else
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
        OnPropertyChanged(nameof(HasOpenTabs));
    }

    /// <summary>Closes every page except the selected tab.</summary>
    [RelayCommand]
    public void CloseOtherTabs()
    {
        if (SelectedTab is null)
            return;

        var keep = SelectedTab.Key;
        foreach (var tab in Tabs.Where(t => t.Key != keep && t.CanClose).ToList())
            Tabs.Remove(tab);

        StatusText = "تم إغلاق باقي التبويبات";
        OnPropertyChanged(nameof(HasOpenTabs));
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