using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Daftar.ViewModels;

/// <summary>
/// One open tab. A null ViewType means the page has no dedicated screen yet and TabHost renders
/// a placeholder for it.
/// </summary>
public partial class TabViewModel(TabSpec spec) : ObservableObject
{

    /// <summary>Identity, so a page opens once and is then focused rather than duplicated.</summary>
    public string Key => spec.Key;

    public Type? ViewType => spec.ViewType;

    public Type? ViewModelType => spec.ViewModelType;

    /// <summary>What the page does. Shown by the placeholder until the page is built.</summary>
    public string? Caption => spec.Caption;

    /// <summary>Locked in when first opened; the home page has no close button.</summary>
    public bool CanClose => spec.CanClose;

    [ObservableProperty]
    private string _title = spec.Title;

    public string Icon => TabKeys.IconFor(Key);

    public void SetCaption(string caption)
    {
        if (!string.IsNullOrWhiteSpace(caption))
            Title = caption;
    }
}

/// <summary>
/// Everything needed to open a page as a tab. The view model type is named explicitly rather than
/// derived from the view's name: reflection that fails silently leaves the page blank with no
/// error, which is exactly the kind of bug that wastes an afternoon.
/// </summary>
public sealed record TabSpec(
    string Key,
    string Title,
    Type? ViewType,
    Type? ViewModelType,
    string? Caption = null,
    bool CanClose = true);

/// <summary>The pages that can be opened as tabs.</summary>
public static class TabKeys
{
    public const string Dashboard = "dashboard";
    public const string Products = "products";
    public const string Receive = "receive";
    public const string Sell = "sell";
    public const string Returns = "returns";
    public const string Payments = "payments";
    public const string Stock = "stock";
    public const string Parties = "parties";
    public const string Sessions = "sessions";
    public const string Reports = "reports";
    public const string Settings = "settings";
    public const string Errors = "errors";

    /// <summary>Launcher strip order, right to left.</summary>
    public static readonly string[] Order =
    [
        Sell, Receive, Returns, Products, Stock,
        Payments, Parties, Sessions, Reports, Settings, Errors,
    ];

    public static string TitleFor(string key) => key switch
    {
        Dashboard => "الرئيسية",
        Products => "المنتجات",
        Receive => "الاستلام",
        Sell => "البيع",
        Returns => "المرتجعات",
        Payments => "المقبوضات",
        Stock => "المخزون",
        Parties => "الأطراف",
        Sessions => "الموزعين",
        Reports => "التقارير",
        Settings => "الإعدادات",
        Errors => "الأخطاء",
        _ => key,
    };

    public static string IconFor(string key) => key switch
    {
        Sell => "🛒",
        Products => "🏷️",
        Receive => "📥",
        Returns => "🔄",
        Payments => "💵",
        Stock => "📦",
        Parties => "👥",
        Sessions => "🚚",
        Reports => "📈",
        Settings => "⚙️",
        Errors => "⚠️",
        _ => "📄",
    };
}