using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public sealed record UserListItem(
    int Id,
    string Username,
    string FullName,
    int RoleId,
    string RoleName,
    bool IsActive,
    string StatusText,
    string StatusColor,
    string LastLoginText);

public sealed record RoleListItem(
    int Id,
    string Name,
    decimal MaxDiscountPercent,
    string MaxDiscountText,
    int PermissionsCount,
    string PermissionsSummary);

public sealed record RoleOption(int Id, string Name);

public partial class PermissionCheckItem : ObservableObject
{
    public string Code { get; }
    public string Title { get; }
    public string Description { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PermissionCheckItem(string code, string title, string description, bool isSelected = false)
    {
        Code = code;
        Title = title;
        Description = description;
        _isSelected = isSelected;
    }
}

public partial class SettingsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    // ── Navigation Section ────────────────────────────────────────────────────
    [ObservableProperty] private int _selectedSectionIndex; // 0=Company, 1=Users, 2=Roles, 3=Backup

    public bool IsCompanySectionActive => SelectedSectionIndex == 0;
    public bool IsUsersSectionActive => SelectedSectionIndex == 1;
    public bool IsRolesSectionActive => SelectedSectionIndex == 2;
    public bool IsBackupSectionActive => SelectedSectionIndex == 3;

    [RelayCommand] public void SelectCompanySection() => SelectedSectionIndex = 0;
    [RelayCommand] public void SelectUsersSection() => SelectedSectionIndex = 1;
    [RelayCommand] public void SelectRolesSection() => SelectedSectionIndex = 2;
    [RelayCommand] public void SelectBackupSection() => SelectedSectionIndex = 3;

    partial void OnSelectedSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCompanySectionActive));
        OnPropertyChanged(nameof(IsUsersSectionActive));
        OnPropertyChanged(nameof(IsRolesSectionActive));
        OnPropertyChanged(nameof(IsBackupSectionActive));

        if (value == 3)
            RefreshBackups();
    }

    // ── Company Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private string _companyName   = string.Empty;
    [ObservableProperty] private string _phone         = string.Empty;
    [ObservableProperty] private string _address       = string.Empty;
    [ObservableProperty] private string _taxNumber     = string.Empty;
    [ObservableProperty] private string _receiptFooter = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _backupStatusMessage = string.Empty;
    [ObservableProperty] private string _lastBackupPath = string.Empty;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _isBackupBusy;

    // ── Users & Roles Collections ─────────────────────────────────────────────
    public ObservableCollection<UserListItem> UsersList { get; } = [];
    public ObservableCollection<RoleListItem> RolesList { get; } = [];
    public ObservableCollection<RoleOption> RoleOptions { get; } = [];

    // ── User Dialog State ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isUserDialogOpen;
    [ObservableProperty] private bool _isUserEditMode;
    [ObservableProperty] private int? _editingUserId;
    [ObservableProperty] private string _userDialogTitle = "إضافة مستخدم جديد";
    [ObservableProperty] private string _userDialogError = string.Empty;

    [ObservableProperty] private string _userFormUsername = string.Empty;
    [ObservableProperty] private string _userFormFullName = string.Empty;
    [ObservableProperty] private string _userFormPassword = string.Empty;
    [ObservableProperty] private RoleOption? _userFormSelectedRole;
    [ObservableProperty] private bool _userFormIsActive = true;
    [ObservableProperty] private bool _userFormMustChangePassword;

    // ── Role Dialog State ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isRoleDialogOpen;
    [ObservableProperty] private bool _isRoleEditMode;
    [ObservableProperty] private int? _editingRoleId;
    [ObservableProperty] private string _roleDialogTitle = "إضافة دور جديد";
    [ObservableProperty] private string _roleDialogError = string.Empty;

    [ObservableProperty] private string _roleFormName = string.Empty;
    [ObservableProperty] private string _roleFormMaxDiscount = "0";
    public ObservableCollection<PermissionCheckItem> RolePermissions { get; } = [];

    public SettingsViewModel() : this(App.Services!) { }

    [RelayCommand]
    public async Task InitializeAsync() => await LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري التحميل...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            // Load Company Settings
            var settings = await db.Settings.AsNoTracking().ToListAsync();
            CompanyName   = Get(settings, Setting.Keys.CompanyName);
            Phone         = Get(settings, Setting.Keys.Phone);
            Address       = Get(settings, Setting.Keys.Address);
            TaxNumber     = Get(settings, Setting.Keys.TaxNumber);
            ReceiptFooter = Get(settings, Setting.Keys.ReceiptFooter);

            // Load Users & Roles
            await LoadUsersAndRolesInternalAsync(db);

            StatusMessage = "تم تحميل البيانات بنجاح";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadUsersAndRolesInternalAsync(DaftarContext db)
    {
        // 1. Roles
        var roles = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync();

        RolesList.Clear();
        RoleOptions.Clear();

        foreach (var r in roles)
        {
            RoleOptions.Add(new RoleOption(r.Id, r.Name));

            var codes = r.PermissionCodes;
            var summary = codes.Count switch
            {
                0 => "لا توجد صلاحيات",
                _ when codes.Count >= Role.Codes.All.Length => "كافة الصلاحيات (مسؤول نظام)",
                _ => $"{codes.Count} صلاحيات ({string.Join(", ", codes.Take(3))}...)"
            };

            RolesList.Add(new RoleListItem(
                r.Id,
                r.Name,
                r.MaxDiscountPercent,
                $"{r.MaxDiscountPercent:N0}%",
                codes.Count,
                summary));
        }

        // 2. Users
        var users = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .OrderBy(u => u.Username)
            .ToListAsync();

        UsersList.Clear();
        foreach (var u in users)
        {
            var statusText = u.IsActive ? "نشط" : "معطل";
            var statusColor = u.IsActive ? "#107C41" : "#D13438";
            var lastLogin = u.LastLoginAt.HasValue
                ? u.LastLoginAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "لم يدخل بعد";

            UsersList.Add(new UserListItem(
                u.Id,
                u.Username,
                u.FullName,
                u.RoleId,
                u.Role?.Name ?? "غير محدد",
                u.IsActive,
                statusText,
                statusColor,
                lastLogin));
        }
    }

    public async Task RefreshUsersAndRolesAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await LoadUsersAndRolesInternalAsync(db);
    }

    // ── Company Settings Actions ──────────────────────────────────────────────
    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري الحفظ...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            await Upsert(db, Setting.Keys.CompanyName,   CompanyName);
            await Upsert(db, Setting.Keys.Phone,         Phone);
            await Upsert(db, Setting.Keys.Address,       Address);
            await Upsert(db, Setting.Keys.TaxNumber,     TaxNumber);
            await Upsert(db, Setting.Keys.ReceiptFooter, ReceiptFooter);

            await db.SaveChangesAsync();
            StatusMessage = "تم حفظ الإعدادات بنجاح ✓";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ في الحفظ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Get(List<Setting> list, string key)
        => list.Find(s => s.Key == key)?.Value ?? string.Empty;

    private static async Task Upsert(DaftarContext db, string key, string value)
    {
        var row = await db.Settings.FindAsync(key);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value     = value;
            row.UpdatedAt = DateTime.UtcNow;
            db.Settings.Update(row);
        }
    }

    // ── Backup Actions ────────────────────────────────────────────────────────

    /// <summary>Snapshots on disk, newest first. Rebuilt whenever the backup section opens.</summary>
    public ObservableCollection<BackupFile> Backups { get; } = [];

    /// <summary>The snapshot queued for the next start, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingRestore))]
    private BackupFile? _pendingRestore;

    public bool HasPendingRestore => PendingRestore is not null;

    public bool HasNoBackups => Backups.Count == 0;

    private BackupStore Store => BackupStore.ForApp;

    /// <summary>
    /// Re-reads the folder. Called when the section opens so the list is current without a
    /// manual refresh button the user has to know about.
    /// </summary>
    [RelayCommand]
    public void RefreshBackups()
    {
        var store = Store;

        Backups.Clear();
        foreach (var file in store.List())
            Backups.Add(file);

        OnPropertyChanged(nameof(HasNoBackups));

        var pendingPath = store.PendingRestore();
        PendingRestore = pendingPath is null
            ? null
            : Backups.FirstOrDefault(b => b.Path == pendingPath);
    }

    [RelayCommand]
    public async Task CreateBackupAsync()
    {
        IsBackupBusy = true;
        BackupStatusMessage = "جاري إنشاء النسخة الاحتياطية...";
        try
        {
            await Task.Yield();

            if (!System.IO.File.Exists(DaftarPaths.DatabasePath))
            {
                BackupStatusMessage = "ملف قاعدة البيانات غير موجود بعد.";
                return;
            }

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var created = await Store.CreateAsync(db);
            var pruned = Store.Prune();

            LastBackupPath = created.Path;
            RefreshBackups();

            BackupStatusMessage = pruned > 0
                ? $"تم حفظ النسخة الاحتياطية بنجاح ✓، وتم مسح {pruned} نسخة قديمة."
                : "تم حفظ النسخة الاحتياطية بنجاح ✓";
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"خطأ أثناء أخذ النسخة: {ex.Message}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    /// <summary>
    /// Queues a snapshot. It cannot be applied here — the database file is open behind this
    /// screen — so the operator is told plainly what to do next instead of being left waiting.
    /// </summary>
    [RelayCommand]
    public void ScheduleRestore(BackupFile? file)
    {
        if (file is null)
        {
            BackupStatusMessage = "اختار نسخة أول.";
            return;
        }

        try
        {
            Store.ScheduleRestore(file.Path);
            RefreshBackups();

            BackupStatusMessage =
                "الاستعادة اتجدولت. اقفل دفتر وافتحه تاني، وهترجع البيانات للحالة دي.";
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"مقدرتش أجدول الاستعادة: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CancelRestore()
    {
        Store.CancelPendingRestore();
        RefreshBackups();

        BackupStatusMessage = "الاستعادة اتلغت.";
    }

    // ── User Management Actions ───────────────────────────────────────────────
    [RelayCommand]
    public void OpenAddUserDialog()
    {
        IsUserEditMode = false;
        EditingUserId = null;
        UserDialogTitle = "إضافة مستخدم جديد";
        UserDialogError = string.Empty;

        UserFormUsername = string.Empty;
        UserFormFullName = string.Empty;
        UserFormPassword = string.Empty;
        UserFormIsActive = true;
        UserFormMustChangePassword = false;
        UserFormSelectedRole = RoleOptions.FirstOrDefault();

        IsUserDialogOpen = true;
    }

    [RelayCommand]
    public async Task OpenEditUserDialogAsync(object? param = null)
    {
        var target = param as UserListItem;
        if (target is null) return;

        UserDialogError = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var user = await db.Users.FindAsync(target.Id);
            if (user is null) return;

            IsUserEditMode = true;
            EditingUserId = user.Id;
            UserDialogTitle = $"تعديل المستخدم: {user.Username}";

            UserFormUsername = user.Username;
            UserFormFullName = user.FullName;
            UserFormPassword = string.Empty; // Blank means keep existing password
            UserFormIsActive = user.IsActive;
            UserFormMustChangePassword = user.MustChangePassword;
            UserFormSelectedRole = RoleOptions.FirstOrDefault(r => r.Id == user.RoleId);

            IsUserDialogOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CloseUserDialog()
    {
        IsUserDialogOpen = false;
        UserDialogError = string.Empty;
    }

    [RelayCommand]
    public async Task SaveUserAsync()
    {
        var username = UserFormUsername.Trim();
        var fullName = UserFormFullName.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            UserDialogError = "اسم المستخدم مطلوب";
            return;
        }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            UserDialogError = "الاسم الكامل مطلوب";
            return;
        }

        if (UserFormSelectedRole is null)
        {
            UserDialogError = "يرجى اختيار الدور الوظيفي للمستخدم";
            return;
        }

        if (!IsUserEditMode && string.IsNullOrWhiteSpace(UserFormPassword))
        {
            UserDialogError = "يرجى إدخال كلمة المرور للمستخدم الجديد";
            return;
        }

        IsBusy = true;
        UserDialogError = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            // Anyone who can reach this screen could mint an administrator: assign the Admin role,
            // set a password they know, sign in as it. Without this check the whole role model is
            // decorative -- User.Can was defined and never called, and every page was registered
            // unconditionally.
            var authorizer = scope.ServiceProvider.GetRequiredService<IUserAuthorizer>();
            if (!await authorizer.CanAsync(Role.Codes.UsersManage))
            {
                UserDialogError = "ليست عندك صلاحية لإدارة المستخدمين.";
                return;
            }

            // Check username uniqueness
            var currentId = EditingUserId ?? 0;
            var exists = await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower() && u.Id != currentId);
            if (exists)
            {
                UserDialogError = "اسم المستخدم موجود مسبقاً، يرجى اختيار اسم آخر";
                return;
            }

            if (IsUserEditMode && EditingUserId.HasValue)
            {
                var user = await db.Users.FindAsync(EditingUserId.Value);
                if (user is null)
                {
                    UserDialogError = "المستخدم غير موجود";
                    return;
                }

                user.Username = username;
                user.FullName = fullName;
                user.RoleId = UserFormSelectedRole.Id;
                user.IsActive = UserFormIsActive;
                user.MustChangePassword = UserFormMustChangePassword;

                if (!string.IsNullOrWhiteSpace(UserFormPassword))
                {
                    user.PasswordHash = PasswordHasher.Hash(UserFormPassword.Trim());
                }

                user.UpdatedAt = DateTime.UtcNow;
                user.UpdatedBy = 1;

                user.Validate();
                await db.SaveChangesAsync();

                StatusMessage = $"تم تحديث بيانات المستخدم: {user.Username} ✓";
            }
            else
            {
                var user = new User
                {
                    Username = username,
                    FullName = fullName,
                    PasswordHash = PasswordHasher.Hash(UserFormPassword.Trim()),
                    RoleId = UserFormSelectedRole.Id,
                    IsActive = UserFormIsActive,
                    MustChangePassword = UserFormMustChangePassword,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = 1,
                };

                user.Validate();
                db.Users.Add(user);
                await db.SaveChangesAsync();

                StatusMessage = $"تمت إضافة المستخدم: {user.Username} بنجاح ✓";
            }

            IsUserDialogOpen = false;
            await LoadUsersAndRolesInternalAsync(db);
        }
        catch (Exception ex)
        {
            UserDialogError = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleUserActiveAsync(object? param = null)
    {
        var target = param as UserListItem;
        if (target is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var user = await db.Users.FindAsync(target.Id);
            if (user is null) return;

            if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase) && user.IsActive)
            {
                StatusMessage = "لا يمكن تعطيل حساب مسؤول النظام الرئيسي (admin)";
                return;
            }

            user.IsActive = !user.IsActive;
            user.UpdatedAt = DateTime.UtcNow;
            user.UpdatedBy = 1;
            await db.SaveChangesAsync();

            StatusMessage = user.IsActive ? $"تم تفعيل المستخدم: {user.Username}" : $"تم تعطيل المستخدم: {user.Username}";
            await LoadUsersAndRolesInternalAsync(db);
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
        }
    }

    // ── Role & Permission Actions ─────────────────────────────────────────────
    [RelayCommand]
    public void OpenAddRoleDialog()
    {
        IsRoleEditMode = false;
        EditingRoleId = null;
        RoleDialogTitle = "إضافة دور جديد";
        RoleDialogError = string.Empty;

        RoleFormName = string.Empty;
        RoleFormMaxDiscount = "0";

        RolePermissions.Clear();
        foreach (var p in CreateDefaultPermissionCheckItems())
        {
            RolePermissions.Add(p);
        }

        IsRoleDialogOpen = true;
    }

    [RelayCommand]
    public async Task OpenEditRoleDialogAsync(object? param = null)
    {
        var target = param as RoleListItem;
        if (target is null) return;

        RoleDialogError = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var role = await db.Roles.FindAsync(target.Id);
            if (role is null) return;

            IsRoleEditMode = true;
            EditingRoleId = role.Id;
            RoleDialogTitle = $"تعديل الدور: {role.Name}";

            RoleFormName = role.Name;
            RoleFormMaxDiscount = role.MaxDiscountPercent.ToString("G", CultureInfo.InvariantCulture);

            RolePermissions.Clear();
            foreach (var p in CreateDefaultPermissionCheckItems())
            {
                p.IsSelected = role.Has(p.Code);
                RolePermissions.Add(p);
            }

            IsRoleDialogOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CloseRoleDialog()
    {
        IsRoleDialogOpen = false;
        RoleDialogError = string.Empty;
    }

    [RelayCommand]
    public async Task SaveRoleAsync()
    {
        var name = RoleFormName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            RoleDialogError = "اسم الدور مطلوب";
            return;
        }

        if (!decimal.TryParse(RoleFormMaxDiscount, NumberStyles.Number, CultureInfo.InvariantCulture, out var maxDiscount)
            || maxDiscount < 0 || maxDiscount > 100)
        {
            RoleDialogError = "أقصى نسبة خصم يجب أن تكون رقماً بين 0 و 100";
            return;
        }

        IsBusy = true;
        RoleDialogError = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var selectedCodes = RolePermissions
                .Where(p => p.IsSelected)
                .Select(p => p.Code)
                .ToList();

            var permissionsCsv = string.Join(',', selectedCodes);

            if (IsRoleEditMode && EditingRoleId.HasValue)
            {
                var role = await db.Roles.FindAsync(EditingRoleId.Value);
                if (role is null)
                {
                    RoleDialogError = "الدور غير موجود";
                    return;
                }

                role.Name = name;
                role.MaxDiscountPercent = maxDiscount;
                role.Permissions = permissionsCsv;
                role.UpdatedAt = DateTime.UtcNow;
                role.UpdatedBy = 1;

                role.Validate();
                await db.SaveChangesAsync();

                StatusMessage = $"تم تحديث الدور: {role.Name} ✓";
            }
            else
            {
                var role = new Role
                {
                    Name = name,
                    MaxDiscountPercent = maxDiscount,
                    Permissions = permissionsCsv,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = 1,
                };

                role.Validate();
                db.Roles.Add(role);
                await db.SaveChangesAsync();

                StatusMessage = $"تمت إضافة الدور: {role.Name} بنجاح ✓";
            }

            IsRoleDialogOpen = false;
            await LoadUsersAndRolesInternalAsync(db);
        }
        catch (Exception ex)
        {
            RoleDialogError = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One checkbox per code in <see cref="Role.Codes.All"/>, and that has to stay true.
    ///
    /// SaveRoleAsync rebuilds the permission string from these checkboxes alone, so a code with no
    /// checkbox is silently stripped the moment anyone opens and saves a role -- which is how
    /// Purchases.Create and Payments.Manage came to be dropped from real roles. A test pins the
    /// correspondence so the next code added cannot repeat it.
    /// </summary>
    private static List<PermissionCheckItem> CreateDefaultPermissionCheckItems()
    {
        return
        [
            new(Role.Codes.SalesCreate, "إنشاء فواتير البيع", "إمكانية إدخال وإصدار فواتير بيع جديدة"),
            new(Role.Codes.PurchasesCreate, "إنشاء فواتير الشراء", "إدخال وإصدار فواتير شراء من الموردين"),
            new(Role.Codes.PaymentsManage, "إدارة سندات القبض والدفع", "تحصيل المبالغ من العملاء ودفعها للموردين"),
            new(Role.Codes.PriceOverride, "تعديل أسعار البيع", "تغيير سعر بيع الصنف يدوياً عند البيع"),
            new(Role.Codes.StockAdjust, "تسوية المخزون", "تعديل كميات وأرصدة الأصناف في المستودع"),
            new(Role.Codes.StockScrap, "تسجيل هالك وتوالف", "إثبات تالف المخزون وتنزيله من الأرصدة"),
            new(Role.Codes.PartiesEditCredit, "تعديل حدود الائتمان", "تعديل سقف البيع الآجل للعملاء والموردين"),
            new(Role.Codes.ReportsCost, "عرض التكلفة والربحية", "الاطلاع على أرباح وتكاليف البضاعة في التقارير"),
            new(Role.Codes.UsersManage, "إدارة المستخدمين", "إنشاء وتعديل حسابات المستخدمين وصلاحياتهم"),
        ];
    }
}
