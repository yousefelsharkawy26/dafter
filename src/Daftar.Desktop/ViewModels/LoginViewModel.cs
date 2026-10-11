using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

public partial class LoginViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _mustChangePassword;
    [ObservableProperty] private bool _rememberUsername = true;

    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _succeeded;

    [ObservableProperty] private bool _revealPassword;
    [ObservableProperty] private bool _revealNewPassword;
    [ObservableProperty] private bool _revealConfirmPassword;

    public char PasswordChar => RevealPassword ? '\0' : '●';
    public char NewPasswordChar => RevealNewPassword ? '\0' : '●';
    public char ConfirmPasswordChar => RevealConfirmPassword ? '\0' : '●';

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool HasValidNewPasswordLength => !string.IsNullOrEmpty(NewPassword) && NewPassword.Length >= 6;
    public bool PasswordsMatch => !string.IsNullOrEmpty(NewPassword) && NewPassword == ConfirmPassword;

    public ObservableCollection<string> AvailableUsers { get; } = [];

    partial void OnRevealPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));
    partial void OnRevealNewPasswordChanged(bool value) => OnPropertyChanged(nameof(NewPasswordChar));
    partial void OnRevealConfirmPasswordChanged(bool value) => OnPropertyChanged(nameof(ConfirmPasswordChar));

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnUsernameChanged(string value)
    {
        if (!_busy && !string.IsNullOrEmpty(Message)) Message = string.Empty;
    }

    partial void OnPasswordChanged(string value)
    {
        if (!_busy && !string.IsNullOrEmpty(Message)) Message = string.Empty;
    }

    partial void OnNewPasswordChanged(string value)
    {
        if (!string.IsNullOrEmpty(Message)) Message = string.Empty;
        OnPropertyChanged(nameof(HasValidNewPasswordLength));
        OnPropertyChanged(nameof(PasswordsMatch));
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        if (!string.IsNullOrEmpty(Message)) Message = string.Empty;
        OnPropertyChanged(nameof(PasswordsMatch));
    }

    /// <summary>Set on success so the shell can show the signed-in user and their permissions.</summary>
    public Domain.Security.User? SignedInUser { get; private set; }

    public Domain.Security.Role? Role { get; private set; }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var users = await ctx.Users
                .Where(u => u.IsActive)
                .OrderBy(u => u.Username)
                .Select(u => u.Username)
                .ToListAsync(CancellationToken.None);

            AvailableUsers.Clear();
            foreach (var u in users)
            {
                AvailableUsers.Add(u);
            }

            if (string.IsNullOrWhiteSpace(Username) && AvailableUsers.Count > 0)
            {
                Username = AvailableUsers[0];
            }
        }
        catch
        {
            // Fallback gracefully if database is still starting
        }
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => RevealPassword = !RevealPassword;

    [RelayCommand]
    private void ToggleNewPasswordVisibility() => RevealNewPassword = !RevealNewPassword;

    [RelayCommand]
    private void ToggleConfirmPasswordVisibility() => RevealConfirmPassword = !RevealConfirmPassword;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Message = "يرجى إدخال اسم المستخدم وكلمة المرور";
            return;
        }

        Busy = true;
        Message = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var audit = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var user = await ctx.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(
                    u => u.Username.ToLower() == Username.Trim().ToLower(), CancellationToken.None);

            if (user is null || !PasswordHasher.Verify(Password, user.PasswordHash))
            {
                audit.AuditLogs.Add(new Domain.Security.AuditLog
                {
                    At = DateTime.UtcNow,
                    UserId = user?.Id,
                    Action = Domain.Security.AuditAction.LoginFailed,
                    EntityName = nameof(Daftar.Domain.Security.User),
                    EntityId = Username.Trim(),
                });
                await audit.SaveChangesAsync();

                Message = "اسم المستخدم أو كلمة المرور غير صحيحة";
                Password = string.Empty;
                return;
            }

            if (!user.IsActive)
            {
                Message = "هذا الحساب موقوف، تواصل مع مدير النظام";
                return;
            }

            user.RecordLogin();
            audit.AuditLogs.Add(new Domain.Security.AuditLog
            {
                At = DateTime.UtcNow,
                UserId = user.Id,
                Action = Domain.Security.AuditAction.Login,
                EntityName = nameof(Daftar.Domain.Security.User),
                EntityId = user.Id.ToString(),
            });
            await audit.SaveChangesAsync();

            // Only after the credentials check out and before anything else stamps work. Set
            // here rather than earlier so a rejected sign-in leaves no identity behind.
            (_services.GetService(typeof(ICurrentUser)) as CurrentUser)?.SignIn(user.Id);

            SignedInUser = user;
            Role = user.Role;

            if (user.MustChangePassword)
            {
                MustChangePassword = true;
                Message = "يجب تغيير كلمة المرور عند أول تسجيل دخول للأمان";
                return;
            }

            Succeeded = true;
        }
        catch (Exception ex)
        {
            _services.Report("Login.SignInAsync", ex);
            Message = $"خطأ أثناء تسجيل الدخول: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            Message = "يرجى كتابة كلمة المرور الجديدة";
            return;
        }

        if (NewPassword.Length < 6)
        {
            Message = "كلمة المرور يجب ألا تقل عن ٦ أحرف";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            Message = "كلمتا المرور غير متطابقتين";
            return;
        }

        if (SignedInUser is null)
            return;

        Busy = true;
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == SignedInUser.Id);
            if (user is null)
            {
                Message = "تعذر العثور على حساب المستخدم";
                return;
            }

            user.PasswordHash = PasswordHasher.Hash(NewPassword);
            user.MustChangePassword = false;
            await ctx.SaveChangesAsync();

            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            MustChangePassword = false;
            Succeeded = true;
        }
        catch (Exception ex)
        {
            _services.Report("Login.ChangePasswordAsync", ex);
            Message = $"خطأ أثناء حفظ كلمة المرور: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        MustChangePassword = false;
        Message = string.Empty;
        SignedInUser = null;
    }
}