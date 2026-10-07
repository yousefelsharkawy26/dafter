using Daftar.Desktop.Receiving;
using Daftar.Domain.Catalog;
using Daftar.Domain.Parties;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The invoice scanner driving the الاستلام screen, against the real OCR engine if it is installed
/// on this machine.
///
/// The image is generated rather than checked in: it is a plain Arabic invoice with the same
/// characteristics as a real one, and generating it keeps the fixture readable and diffable.
/// Without Tesseract installed these skip rather than fail, because the engine is a machine-level
/// dependency — <see cref="InvoiceParserTests"/> covers the parsing rules unconditionally.
/// </summary>
public sealed class InvoiceScanScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-scan-{Guid.NewGuid():N}.db");

    private readonly string _invoicePath = Path.Combine(
        Path.GetTempPath(), $"daftar-invoice-{Guid.NewGuid():N}.png");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await DaftarDatabase.InitializeAsync(ctx, PasswordHasher.Hash("admin"));

        ctx.Parties.Add(new Party { Code = "SUP-1", Name = "النيل للأغذية", IsSupplier = true });

        var catalogue = new[]
        {
            ("OLIVE-1", "زيت زيتون صفيحة", "زجاجة", 1m, 75m),
            ("RICE-1", "ارز مصري فاخر", "كيس", 1m, 90m),
            ("MILK-1", "لبن جهينة 1 لتر", "كرتون", 1m, 55m),
            ("SUGAR-1", "سكر ناعم 2 كجم", "كيس", 1m, 85m),
            ("TEA-1", "شاي ليبتون 25 كيس", "علبة", 1m, 210m),
        };

        var priceListId = await ctx.PriceLists.Where(p => p.IsDefault).Select(p => p.Id).FirstAsync();
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var (sku, name, unitName, factor, price) in catalogue)
        {
            var product = new Product(sku, name);
            product.Units.Add(new ProductUnit
            {
                Name = unitName,
                Factor = factor,
                IsBase = true,
                IsDefaultSale = true,
                IsDefaultPurchase = true,
            });

            ctx.Products.Add(product);
            await ctx.SaveChangesAsync();

            product.Units[0].ProductId = product.Id;
            await ctx.SaveChangesAsync();

            ctx.ProductPrices.Add(new Domain.Sales.ProductPrice
            {
                PriceListId = priceListId,
                ProductUnitId = product.Units[0].Id,
                Price = price,
                EffectiveFrom = today,
            });

            await ctx.SaveChangesAsync();
        }

        WriteInvoiceImage();
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _invoicePath })
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Draws an Arabic invoice with PIL if it is available. Shaped and laid out right-to-left, the
    /// way a printed invoice actually looks.
    /// </summary>
    private void WriteInvoiceImage()
    {
        const string fontPath = "/home/elsharkawy/Desktop/Dafter/src/Daftar.Desktop/Assets/Fonts/Cairo.ttf";

        if (!File.Exists(fontPath))
            return;

        try
        {
            WriteWithPython(fontPath);
        }
        catch
        {
            // No PIL here; the OCR test below skips rather than failing.
        }
    }

    private void WriteWithPython(string fontPath)
    {
        var script = Path.Combine(Path.GetTempPath(), $"daftar-invoice-{Guid.NewGuid():N}.py");

        File.WriteAllText(script, $$"""
            from PIL import Image, ImageDraw, ImageFont
            FONT = r"{{fontPath}}"
            W, H = 1000, 560
            img = Image.new("RGB", (W, H), "white")
            d = ImageDraw.Draw(img)

            def rtl(x_right, text, size, y):
                f = ImageFont.truetype(FONT, size)
                d.text((x_right, y), text, font=f, fill="black", direction="rtl",
                       language="ar", anchor="ra")

            rtl(W - 40, "فاتورة مورد", 30, 30)
            rtl(W - 40, "رقم الفاتورة: INV-2026-0412", 22, 90)

            hdr = 170
            rtl(W - 40, "الصنف", 22, hdr)
            rtl(W - 380, "الكمية", 22, hdr)
            rtl(W - 560, "السعر", 22, hdr)
            rtl(W - 780, "الإجمالي", 22, hdr)

            rows = [
                ("زيت زيتون صفيحة", "12", "75.00", "900.00"),
                ("لبن جهينة 1 لتر", "24", "55.00", "1320.00"),
                ("سكر ناعم 2 كجم", "30", "85.00", "2550.00"),
                ("شاي ليبتون 25 كيس", "15", "210.00", "3150.00"),
            ]

            y = 220
            for name, qty, price, total in rows:
                rtl(W - 40, name, 22, y)
                rtl(W - 380, qty, 22, y)
                rtl(W - 560, price, 22, y)
                rtl(W - 780, total, 22, y)
                y += 50

            img.save(r"{{_invoicePath}}")
            """);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(script);

        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(30_000);

        File.Delete(script);
    }

    private async Task<ReceiveViewModel> NewScreenAsync()
    {
        var vm = new ReceiveViewModel(_fixture.Services);
        await vm.LoadAsync();
        return vm;
    }

    [SkippableFact]
    public async Task Scanning_a_photo_fills_the_review_list_and_never_posts_by_itself()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();

        var before = vm.Lines.Count;
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.True(vm.HasScan);
        Assert.NotEmpty(vm.Scanned);

        // Reading a photo must never touch the receipt itself.
        Assert.Equal(before, vm.Lines.Count);
        Assert.False(vm.CanPost, "a scan is a proposal; the manager still has to confirm it");
    }

    [SkippableFact]
    public async Task The_invoice_number_on_the_supplier_document_replaces_the_generated_one()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();

        Assert.StartsWith("INV-", vm.InvoiceNo);

        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal("INV-2026-0412", vm.InvoiceNo);
    }

    [SkippableFact]
    public async Task A_confident_line_is_pre_matched_to_its_product()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Scanned);

        // The point of the matcher: an obvious row should not need choosing by hand.
        var confident = vm.Scanned.Where(r => r.MatchScore >= 90).ToList();

        Assert.NotEmpty(confident);
        Assert.All(confident, r =>
        {
            Assert.NotNull(r.Product);
            Assert.NotNull(r.Unit);
            Assert.True(r.IsReady);
        });

        // Every row must offer the catalogue, so a wrong match can be corrected.
        Assert.All(vm.Scanned, r => Assert.NotEmpty(r.Choices));
    }

    [SkippableFact]
    public async Task Choosing_a_product_by_hand_also_chooses_its_unit()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        var row = vm.Scanned.First();

        // Clear it the way an unidentified row arrives: no product, so no unit either.
        row.Product = null;
        Assert.Null(row.Unit);
        Assert.False(row.IsReady);

        // Picking a product by hand must pick its purchase unit too, or the row can never
        // become ready and confirm silently adds nothing.
        row.Product = row.Choices.First();

        Assert.NotNull(row.Unit);
        Assert.NotEmpty(row.AvailableUnits);
        Assert.True(row.IsReady);
    }

    [SkippableFact]
    public async Task A_row_with_no_product_chosen_is_never_added()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.SelectedSupplier = vm.Suppliers.First();
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        foreach (var row in vm.Scanned)
            row.Product = null;

        vm.ConfirmScanCommand.Execute(null);

        Assert.Empty(vm.Lines);
        Assert.Contains("اختار الصنف", vm.ScanStatus);
    }

    [SkippableFact]
    public async Task ARowWithNoReadablePrice_IsNotAddable()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        var row = vm.Scanned.First();
        row.Product = row.Choices.First();

        // OCR missing a price leaves zero. Adding that line kept the receipt total at zero,
        // which silently blocked saving with every field on screen looking filled.
        row.UnitPrice = 0m;

        Assert.True(row.NeedsPrice);
        Assert.False(row.IsReady);

        row.UnitPrice = 25m;

        Assert.False(row.NeedsPrice);
        Assert.True(row.IsReady);
    }

    [SkippableFact]
    public async Task UnreadableRowsAreReported_NotSilentlyDropped()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.SelectedSupplier = vm.Suppliers.First();
        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        foreach (var row in vm.Scanned)
        {
            row.Product ??= row.Choices.First();
            row.UnitPrice = 0m;
        }

        vm.ConfirmScanCommand.Execute(null);

        Assert.Empty(vm.Lines);
        Assert.Contains("اختار الصنف", vm.ScanStatus);
    }

    [SkippableFact]
    public async Task Confirmed_rows_land_on_the_receipt_with_their_figures()
    {
        Skip.IfNot(File.Exists(_invoicePath), "PIL is not available to render the test invoice");
        Skip.IfNot(new OcrEngine().IsAvailable, "Tesseract is not installed on this machine");

        var vm = await NewScreenAsync();
        vm.SelectedSupplier = vm.Suppliers.First();

        vm.PickImage = () => Task.FromResult<string?>(_invoicePath);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Scanned);

        // Only rows the matcher was confident about can be confirmed; anything else is skipped
        // rather than guessed at.
        vm.ConfirmScanCommand.Execute(null);

        Assert.Empty(vm.Scanned);
        Assert.False(vm.HasScan);
        Assert.NotEmpty(vm.Lines);
        Assert.All(vm.Lines, l => Assert.True(l.Quantity > 0m));
    }

    [Fact]
    public async Task A_missing_engine_is_reported_in_arabic_rather_than_crashing()
    {
        var vm = await NewScreenAsync();

        // Point the screen at an engine that is not there, which is what a fresh machine looks like.
        var engine = new OcrEngine("/does/not/exist", null);
        Assert.False(engine.IsAvailable);

        var (invoice, result) = await engine.ScanAsync(_invoicePath);

        Assert.False(result.Ok);
        Assert.Equal(OcrFailure.EngineMissing, result.Failure);
        Assert.Contains("apt install", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoice.Lines);
    }

    [Fact]
    public async Task Confirming_with_nothing_selected_adds_nothing()
    {
        var vm = await NewScreenAsync();

        vm.ConfirmScanCommand.Execute(null);

        Assert.Empty(vm.Lines);
        Assert.Contains("اختار الصنف", vm.ScanStatus);
    }
}