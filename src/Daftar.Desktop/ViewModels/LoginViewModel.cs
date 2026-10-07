using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public partial class LoginViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _mustChangePassword;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private bool _succeeded;

    /// <summary>Set on success so the shell can show the signed-in user and their permissions.</summary>
    public Domain.Security.User? SignedInUser { get; private set; }

    public Domain.Security.Role? Role { get; private set; }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Message = "اكتب اسم المستخدم وكلمة المرور";
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
                // Audited even for a username that does not exist.
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
                Message = "هذا الحساب موقوف";
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

            // Tell the audit interceptor who is acting from here on.
            if (_services.GetService(typeof(ICurrentUser)) is CurrentUser current)
                current.UserId = user.Id;

            SignedInUser = user;
            Role = user.Role;

            if (user.MustChangePassword)
            {
                MustChangePassword = true;
                Message = "لازم تغيّر كلمة المرور قبل ما تكمل";
                return;
            }

            Succeeded = true;
        }
        catch (Exception ex)
        {
            Message = $"خطأ: {ex.Message}";
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
            Message = "اكتب كلمة المرور الجديدة";
            return;
        }

        if (NewPassword.Length < 6)
        {
            Message = "كلمة المرور لازم تكون ٦ حروف على الأقل";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            Message = "كلمتا المرور مش متطابقتين";
            return;
        }

        if (SignedInUser is null)
            return;

        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == SignedInUser.Id);
        if (user is null)
        {
            Message = "الحساب مش موجود";
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

    [RelayCommand]
    private void Back()
    {
        MustChangePassword = false;
        Message = string.Empty;
        SignedInUser = null;
    }
}