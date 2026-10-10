using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure;

/// <summary>First-run seed. Idempotent: safe to call on every start.</summary>
public static class Seed
{
    public static async Task SeedAsync(DaftarContext context, string adminPasswordHash, CancellationToken ct = default)
    {
        await SeedSettingsAsync(context, ct);
        await SeedLocationAsync(context, ct);
        await SeedSequencesAsync(context, ct);
        await SeedWalkInCustomerAsync(context, ct);
        await SeedSalesMasterAsync(context, ct);
        await SeedCashAccountsAsync(context, ct);
        await SeedAdminAsync(context, adminPasswordHash, ct);
    }

    /// <summary>A default price list and a cash account, so a sale can be entered immediately.</summary>
    private static async Task SeedSalesMasterAsync(DaftarContext context, CancellationToken ct)
    {
        if (await context.PriceLists.AnyAsync(ct))
            return;

        var priceList = new PriceList { Name = "Standard", IsDefault = true, IsActive = true };
        context.PriceLists.Add(priceList);
        await context.SaveChangesAsync(ct);


        var setting = await context.Settings
            .FirstOrDefaultAsync(s => s.Key == Setting.Keys.DefaultPriceListId, ct);

        if (setting is null)
        {
            context.Settings.Add(new Setting
            {
                Key = Setting.Keys.DefaultPriceListId,
                Value = priceList.Id.ToString(),
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            setting.Value = priceList.Id.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The accounts a food distribution business actually collects through. Editable later from
    /// الإعدادات; these are just the starting set.
    /// </summary>
    private static readonly CashAccount[] DefaultCashAccounts =
    [
        new() { Name = "الكاشير", Type = CashAccountType.Cash },
        new() { Name = "فودافون كاش", Type = CashAccountType.Wallet },
        new() { Name = "انستاباي", Type = CashAccountType.Wallet },
    ];

    /// <summary>
    /// Adds any missing money accounts. Separate from the price list so an existing database
    /// picks them up: they were introduced after the first run.
    /// </summary>
    private static async Task SeedCashAccountsAsync(DaftarContext context, CancellationToken ct)
    {
        var existing = await context.CashAccounts.Select(a => a.Name).ToListAsync(ct);
        var missing = DefaultCashAccounts.Where(a => !existing.Contains(a.Name)).ToList();

        if (missing.Count == 0)
            return;

        context.CashAccounts.AddRange(missing);
        await context.SaveChangesAsync(ct);
    }

    private static async Task SeedSettingsAsync(DaftarContext context, CancellationToken ct)
    {
        var defaults = new (string Key, string Value)[]
        {
            (Setting.Keys.CompanyName, "دفتر"),
            (Setting.Keys.TaxNumber, string.Empty),
            (Setting.Keys.Phone, string.Empty),
            (Setting.Keys.Address, string.Empty),
            (Setting.Keys.ReceiptFooter, "شكراً لتعاملكم معنا"),
            (Setting.Keys.LockedUntilDate, DateOnly.MinValue.ToString("yyyy-MM-dd")),
        };

        var existing = await context.Settings
            .Where(s => defaults.Select(d => d.Key).Contains(s.Key))
            .ToListAsync(ct);

        var existingKeys = existing.Select(s => s.Key).ToHashSet();
        var now = DateTime.UtcNow;

        foreach (var (key, value) in defaults)
        {
            if (existingKeys.Contains(key))
            {
                var row = existing.First(s => s.Key == key);
                if (key == Setting.Keys.ReceiptFooter && row.Value == "Thank you")
                {
                    row.Value = value;
                    row.UpdatedAt = now;
                }
                continue;
            }

            context.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now });
        }

        await context.SaveChangesAsync(ct);
    }

    private static async Task SeedLocationAsync(DaftarContext context, CancellationToken ct)
    {
        if (await context.StockLocations.AnyAsync(ct))
            return;

        context.StockLocations.Add(new StockLocation
        {
            Name = "Main Warehouse",
            Type = StockLocationType.Warehouse,
        });

        await context.SaveChangesAsync(ct);
    }

    private static async Task SeedSequencesAsync(DaftarContext context, CancellationToken ct)
    {
        if (await context.DocumentSequences.AnyAsync(ct))
            return;

        // Year 0 = never resets, which is what a first company wants.
        foreach (var (docType, prefix) in new[]
                 {
                     (DocumentType.Purchase, "PO-"),
                     (DocumentType.PurchaseReturn, "PR-"),
                     (DocumentType.Sale, "SO-"),
                     (DocumentType.SaleReturn, "SR-"),
                     (DocumentType.PaymentIn, "RC-"),
                     (DocumentType.PaymentOut, "PV-"),
                     (DocumentType.Transfer, "TR-"),
                     (DocumentType.Count, "SC-"),
                     (DocumentType.Party, "P-"),
                 })
        {
            context.DocumentSequences.Add(new DocumentSequence
            {
                DocType = docType,
                Year = 0,
                Prefix = prefix,
                NextNumber = 1,
                Padding = 6,
            });
        }

        await context.SaveChangesAsync(ct);
    }

    private static async Task SeedWalkInCustomerAsync(DaftarContext context, CancellationToken ct)
    {
        var existingWalkIn = await context.Parties.FirstOrDefaultAsync(p => p.Code == "WALKIN", ct);
        if (existingWalkIn is not null)
        {
            if (existingWalkIn.Name == "Walk-in customer")
            {
                existingWalkIn.Name = "عميل نقدي";
                await context.SaveChangesAsync(ct);
            }
            return;
        }

        if (await context.Parties.AnyAsync(ct))
            return;

        var walkIn = new Party
        {
            Code = "WALKIN",
            Name = "عميل نقدي",
            IsCustomer = true,
            IsSupplier = false,
            CustomerType = CustomerType.Individual,
            CreditLimit = 0m,
            Status = PartyStatus.Active,
        };

        context.Parties.Add(walkIn);
        await context.SaveChangesAsync(ct);

        var setting = await context.Settings
            .FirstOrDefaultAsync(s => s.Key == Setting.Keys.DefaultCustomerId, ct);

        if (setting is null)
        {
            context.Settings.Add(new Setting
            {
                Key = Setting.Keys.DefaultCustomerId,
                Value = walkIn.Id.ToString(),
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            setting.Value = walkIn.Id.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);
    }

    private static async Task SeedAdminAsync(DaftarContext context, string adminPasswordHash, CancellationToken ct)
    {
        if (await context.Users.AnyAsync(ct))
            return;

        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin", ct);
        if (adminRole is null)
        {
            adminRole = new Role
            {
                Name = "Admin",
                Permissions = Role.Codes.Csv,
                MaxDiscountPercent = 100m,
            };
            context.Roles.Add(adminRole);
            await context.SaveChangesAsync(ct);
        }

        context.Users.Add(new User
        {
            Username = "admin",
            FullName = "Administrator",
            PasswordHash = adminPasswordHash,
            RoleId = adminRole.Id,
            IsActive = true,
            MustChangePassword = true,
        });

        await context.SaveChangesAsync(ct);
    }
}