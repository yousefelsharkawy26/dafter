using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Desktop.Receiving;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public partial class ReceiveViewModel
{
    /// <summary>
    /// Set by the view: shows the file picker and returns the chosen image path, or null when the
    /// manager cancels. The view model cannot reach a StorageProvider on its own.
    /// </summary>
    public Func<Task<string?>>? PickImage { get; set; }

    /// <summary>Rows proposed by the invoice scan, awaiting the manager's confirmation.</summary>
    public ObservableCollection<ScannedRow> Scanned { get; } = [];

    [ObservableProperty] private bool _scanning;

    [ObservableProperty] private string _scanStatus = string.Empty;

    [ObservableProperty] private bool _hasScan;

    /// <summary>
    /// Reads an invoice photo and fills the review list. Nothing is added to the receipt yet: the
    /// manager picks the product for each line, because Arabic OCR reads names only about 60%
    /// correctly while reading the figures almost perfectly.
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (PickImage is null)
        {
            ScanStatus = "اختيار الصور غير متاح";
            return;
        }

        string? path;

        try
        {
            path = await PickImage();
        }
        catch (Exception ex)
        {
            // A picker that throws must say so, not leave the manager tapping a dead button.
            ScanStatus = $"مش قادر يفتح الصور: {ex.Message}";
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            // Cancelling is a normal choice; nothing to report.
            ScanStatus = "";
            return;
        }

        Scanning = true;
        HasScan = false;
        Scanned.Clear();
        ScanStatus = "جاري قراءة الفاتورة…";

        try
        {
            var (invoice, result) = await new OcrEngine().ScanAsync(path);

            if (!result.Ok)
            {
                ScanStatus = result.Failure switch
                {
                    OcrFailure.EngineMissing =>
                        "محرك القراءة غير موجود. ثبّته مرة واحدة: sudo apt install tesseract-ocr tesseract-ocr-ara",
                    OcrFailure.ArabicDataMissing =>
                        "بيانات اللغة العربية ناقصة. ثبّت: sudo apt install tesseract-ocr-ara",
                    OcrFailure.TimedOut => "القراءة اتأخرت — جرّب صورة أوضح",
                    OcrFailure.BadImage => "مش قادر يقرأ حاجة في الصورة دي",
                    _ => $"فشل في القراءة: {result.Detail}",
                };
                return;
            }

            if (invoice.Lines.Count == 0)
            {
                ScanStatus = "مفيش سطور اتقرت — جرّب تصور أوضح أو اكتب السطور يدوي";
                return;
            }

            // The invoice number on the supplier's own document beats the one we generated.
            if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
                InvoiceNo = invoice.InvoiceNumber!;

            if (string.IsNullOrWhiteSpace(SelectedSupplier?.Name) is false
                && !string.IsNullOrWhiteSpace(invoice.SupplierGuess))
            {
                var guess = invoice.SupplierGuess;
                var supplier = Suppliers.FirstOrDefault(s =>
                    s.Name.Contains(guess, StringComparison.OrdinalIgnoreCase)
                    || guess.Contains(s.Name, StringComparison.OrdinalIgnoreCase));

                if (supplier is not null)
                    SelectedSupplier = supplier;
            }

            await BuildScanRowsAsync(invoice);

            HasScan = true;
            ScanStatus = $"اتقرا {Scanned.Count} سطر — راجعه قبل ما تضيفه";
        }
        catch (Exception ex)
        {
            ScanStatus = $"خطأ في القراءة: {ex.Message}";
        }
        finally
        {
            Scanning = false;
        }
    }

    private async Task BuildScanRowsAsync(ScannedInvoice invoice)
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var catalogue = await ctx.Products
            .Where(p => p.IsActive)
            .Select(p => new { p.Id, p.Sku, p.Name })
            .ToListAsync();

        var units = await ctx.ProductUnits
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.ProductId, u.Name, u.Factor, u.IsDefaultPurchase })
            .ToListAsync();

        var entries = catalogue.Select(c => (c.Id, c.Sku, c.Name)).ToList();

        // Every row offers the whole catalogue, so a wrong match can be corrected without
        // reaching up the visual tree from inside a row template.
        ObservableCollection<ProductRow> choices = [.. Products];

        // Units per product, so choosing a product can pick its unit automatically.
        var unitsByProduct = units
            .GroupBy(u => u.ProductId)
            .ToDictionary(
                g => g.Key,
                g => new ObservableCollection<UnitRow>(g
                    .Select(u => new UnitRow(
                        u.Id, u.Name, u.Factor, IsBase: false, IsDefaultPurchase: u.IsDefaultPurchase))
                    .OrderByDescending(u => u.IsDefaultPurchase)));

        foreach (var line in invoice.Lines)
        {
            var best = ProductMatcher.Rank(line.Text, entries).FirstOrDefault();

            ProductRow? product = null;
            UnitRow? unit = null;

            if (best is not null)
            {
                product = Products.FirstOrDefault(p => p.Id == best.ProductId);

                var productUnits = units
                    .Where(u => u.ProductId == best.ProductId)
                    .Select(u => new UnitRow(
                        u.Id, u.Name, u.Factor, IsBase: false, IsDefaultPurchase: u.IsDefaultPurchase))
                    .OrderByDescending(u => u.IsDefaultPurchase)
                    .ToList();

                unit = productUnits.FirstOrDefault();
            }

            Scanned.Add(new ScannedRow
            {
                Choices = choices,
                UnitsByProduct = unitsByProduct,
                Text = line.Text,
                RawText = line.RawText,
                ScannedQuantity = line.Quantity,
                ScannedUnitPrice = line.UnitPrice,
                ScannedTotal = line.Total,
                MatchScore = best?.Score ?? 0,
                Product = product,
                Unit = unit,
                Quantity = line.Quantity ?? 0m,
                UnitPrice = line.UnitPrice ?? 0m,
            });
        }
    }

    /// <summary>Moves every confirmed scan row onto the receipt, then clears the review list.</summary>
    [RelayCommand]
    private void ConfirmScan()
    {
        var ready = Scanned.Where(r => r.IsReady).ToList();

        if (ready.Count == 0)
        {
            ScanStatus = "اختار الصنف لكل سطر قبل الإضافة";
            return;
        }

        foreach (var row in ready)
        {
            var existing = Lines.FirstOrDefault(l =>
                l.ProductId == row.Product!.Id && l.ProductUnitId == row.Unit!.Id);

            if (existing is not null)
                existing.Quantity += row.Quantity;
            else
                Lines.Add(new ReceiveLine
                {
                    ProductId = row.Product!.Id,
                    ProductUnitId = row.Unit!.Id,
                    Sku = row.Product.Sku,
                    ProductName = row.Product.Name,
                    UnitName = row.Unit!.Name,
                    UnitFactor = row.Unit.Factor,
                    Quantity = row.Quantity,
                    UnitPrice = row.UnitPrice,
                });
        }

        Recalculate();

        var skipped = Scanned.Count - ready.Count;
        Status = skipped == 0
            ? $"تمت إضافة {ready.Count} سطر من الفاتورة"
            : $"تمت إضافة {ready.Count} سطر — اتخطي {skipped} سطر ناقص";

        Scanned.Clear();
        HasScan = false;
        ScanStatus = "";
    }

    [RelayCommand]
    private void DiscardScan()
    {
        Scanned.Clear();
        HasScan = false;
        ScanStatus = "";
    }
}

/// <summary>
/// One line the scanner read from the invoice photo. Every field is editable, because the OCR is a
/// starting point and the manager has the last word.
/// </summary>
public partial class ScannedRow : ObservableObject
{
    /// <summary>What OCR read as the product name. Kept visible even after a product is chosen.</summary>
    public string Text { get; init; } = string.Empty;

    public string RawText { get; init; } = string.Empty;

    public decimal? ScannedQuantity { get; init; }

    public decimal? ScannedUnitPrice { get; init; }

    public decimal? ScannedTotal { get; init; }

    /// <summary>How close the best catalogue match was, 0-100.</summary>
    public int MatchScore { get; init; }

    /// <summary>The whole catalogue, so the picker needs no ancestor lookup.</summary>
    public ObservableCollection<ProductRow> Choices { get; init; } = [];

    /// <summary>Units of whichever product is selected, so the unit can follow the product.</summary>
    public ObservableCollection<UnitRow> AvailableUnits { get; } = [];

    /// <summary>All products' units, looked up when a product is chosen.</summary>
    public IReadOnlyDictionary<int, ObservableCollection<UnitRow>> UnitsByProduct { get; init; }
        = new Dictionary<int, ObservableCollection<UnitRow>>();

    /// <summary>Shown next to the row so the manager knows how much to trust it.</summary>
    public string Confidence => MatchScore switch
    {
        >= 90 => "مطابقة قوية",
        >= ProductMatcher.MinimumScore => "مطابقة محتملة",
        _ => "اختار الصنف يدوي",
    };

    [ObservableProperty] private ProductRow? _product;

    [ObservableProperty] private UnitRow? _unit;

    [ObservableProperty] private decimal _quantity;

    [ObservableProperty] private decimal _unitPrice;

    /// <summary>
    /// A price of zero means OCR did not read one, not that the goods were free: adding such a
    /// line left the receipt total at zero, which silently blocked saving. A purchase invoice
    /// with no price on it is always a misread.
    /// </summary>
    public bool IsReady
        => Product is not null && Unit is not null && Quantity > 0m && UnitPrice > 0m;

    /// <summary>
    /// True while the row still has no usable price, so the view can mark it. Reads the
    /// current value, not the one first scanned, so it follows an edit.
    /// </summary>
    public bool NeedsPrice => UnitPrice <= 0m;

    /// <summary>
    /// Choosing a product chooses its purchase unit. Without this the row could never become ready:
    /// a unit was required to add the line but nothing on screen could set one, so every scan row
    /// was silently dropped on confirm.
    /// </summary>
    partial void OnProductChanged(ProductRow? value)
    {
        AvailableUnits.Clear();

        if (value is not null && UnitsByProduct.TryGetValue(value.Id, out var forProduct))
            foreach (var unit in forProduct)
                AvailableUnits.Add(unit);

        Unit = AvailableUnits.FirstOrDefault(u => u.IsDefaultPurchase) ?? AvailableUnits.FirstOrDefault();

        OnPropertyChanged(nameof(IsReady));
    }

    partial void OnUnitChanged(UnitRow? value)
        => OnPropertyChanged(nameof(IsReady));

    partial void OnQuantityChanged(decimal value)
        => OnPropertyChanged(nameof(IsReady));

    partial void OnUnitPriceChanged(decimal value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(NeedsPrice));
    }
}