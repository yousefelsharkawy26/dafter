# Daftar — Plan

Arabic (Egypt) Avalonia desktop inventory/POS app for a food distribution business.
SQLite + EF Core. Single user. 3-tier: `Daftar.Desktop` / `Daftar.Domain` / `Daftar.Infrastructure`.

## Status

- Build: clean (0 warnings, 0 errors)
- Tests: **163/163 pass**
- Nothing committed yet — `Daftar.slnx`, `src/`, `tests/` are all untracked

## Done

**Architecture**
- 3 tiers under `src/`, one solution `Daftar.slnx`
- One shared class set for Domain + EF (no separate EF entities); Domain holds POCOs + invariants, zero EF refs
- Navigation = browser-style tabs, Arabic (Egypt) labels, RTL, single user

**QA CheckReport & Operational Fixes Applied**
- Database Backup & Safety in SettingsView / SettingsViewModel using SQLite `VACUUM INTO` with timestamped local backup files.
- Safe 2-step confirmation for cart clearing in SellView / SellViewModel (prevents accidental invoice wiping).
- Flexible Returns lookup: search original invoices by DocNo, Customer Name, Phone, or Code.
- Stock Transfer UI & logic in StockView / StockViewModel: inter-location transfers (loading/unloading distributor vehicles and warehouse-to-warehouse transfers).
- Counter POS convenience: auto-default to "عميل نقدي" (Cash customer) on open and after posting, and reset quantity to 1 after adding to cart.
- Customer debt indicator in SellView: live customer balance and overdue readout displayed next to customer dropdown.
- Fixed cart and receive line deletion buttons (`RemoveLineCommand` binding corrected from `$parent[Window]` to `$parent[UserControl]`).
- Fixed invisible `tile-value` numbers on light cards by setting default dark foreground with `FlowDirection="LeftToRight"`
- Fixed Dashboard margin/profit calculation by adding `.Include(s => s.Items)` and excluding returns from recent sales
- Fixed Distributors `CK_Distributors_CommissionRate` check constraint via EF migration `FixDistributorCommissionRateCheck` with `CAST AS REAL` (verified with new unit tests for rates 0, 2, 2.5, 5, 50, 99, 100)
- Fixed available stock calculation in `SellViewModel` to filter by selling `_locationId`
- Added full Return creation & posting flow in `ReturnsViewModel` and `ReturnsView` (links to original invoice via `SaleReturnService`)
- Fixed Start Menu ScrollViewer retaining previous scroll offset by resetting offset to 0 on open
- Fixed ProductsView unit boolean fields to render clean CheckBoxes (`DataGridCheckBoxColumn`)
- Fixed default base unit text in product creation form from English `piece` to Arabic `قطعة`
- Fixed Reports collections summary card to display as positive amount
- Fixed app-bar ghost button contrast and PartiesView selected row balance contrast
- Added global unhandled exception logging in `Program.cs`

**Screens (real, working)**
- Dashboard — today's sales/takings/profit, receivables vs payables, ageing
- البيع (Sell) — cart, customer, products, settlement terms
- الاستلام (Receive) — purchase receipt, supplier, settlement terms
- المنتجات (Products) — product + unit management
- المقبوضات (Payments) — standalone In/Out vouchers against any party, outstanding-balance readout, over-collection refused, posted-voucher history
- الأطراف (Parties) — customers & suppliers list, search, filters, balance readouts, account statements, add party form
- المخزون (Stock) — stock balances by lot+location, low-stock filter, location type filter (warehouse/vehicle), value totals
- المرتجعات (Returns) — list of sale returns with link to original invoice, search by DocNo/party
- التقارير (Reports) — date-range profit summary (sales/cost/margin), collections total, overdue receivables list
- الإعدادات (Settings) — company name/phone/address/tax number/receipt footer, persisted to Settings table
- الموزعين (Distributors) — active distributors with vehicle, commission rate/basis, open session indicator
- الجلسات (Sessions) — مركز متكامل لجولات الموزعين: بدء جولة وتسويتها، إضافة موزع والتحكم فيه وتعديله وتفعيله/تعطيله، وتقارير أداء شاملة لكل موزع
- Windows 8 Theme & Start Menu — sharp 0px geometry, Live Tiles mosaic, Windows 8 vector Start button, and Start Screen overlay navigation
- Login

**Integrity & Ledger**
- Stale ledger rows fixed via `20261007140000_RepairStalePurchaseLedgerSigns` (flips sign of pre-fix purchase rows)
- Arabic seed strings self-correcting: `"عميل نقدي"` and `"شكراً لتعاملكم معنا"`
- `IntegrityChecker` called at startup on every launch

## Known issues (open)

None currently open.

## Next

**Housekeeping**
- Commit when the user asks (everything is currently untracked)

## Run

```
dotnet run --project src/Daftar.Desktop
dotnet test Daftar.slnx
```

DB: `~/.local/share/Daftar/daftar.db` (backup at `/tmp/opencode/daftar.db.bak`)

## Gotchas

- This model cannot read PNG images — verify views via sqlite queries + temp log probes, not screenshots
- EF has **no lazy-loading proxies**: any query reading a navigation collection needs an explicit `.Include()`
- View-model tests pass while the UI can still be broken — verify by actually running the app
- Avalonia 12 drift: `AvaloniaLocator` removed; `StatusBar` not built-in; `AutoCompleteBox` has no `BoxItemTemplate`; `Watermark`→`PlaceholderText`; DataGrid needs explicit `StyleInclude` of its Fluent theme or it renders blank
- `x:DataType` on a `DataTemplate` inside `ComboBox.ItemTemplate` must match the option record type
- Python heredocs break on Arabic text / multi-line literals — write scripts to `/tmp/opencode/*.py` with the Write tool
- `pkill -x Daftar.Desktop` (not `-f`, which kills its own shell)
- Pre-existing warning AVLN3001 on `LoginWindow.axaml` (no public constructor) — harmless, left alone
