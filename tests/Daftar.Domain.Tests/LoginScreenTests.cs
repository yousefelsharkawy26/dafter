using System;
using System.IO;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

public sealed class LoginScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-login-test-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await DaftarDatabase.InitializeAsync(ctx, PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EmptyCredentials_SetsValidationMessage()
    {
        var vm = new LoginViewModel(_fixture.Services);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.False(vm.Succeeded);
        Assert.NotEmpty(vm.Message);
        Assert.True(vm.HasMessage);
    }

    [Fact]
    public async Task InvalidPassword_FailsWithErrorMessage()
    {
        var vm = new LoginViewModel(_fixture.Services)
        {
            Username = "admin",
            Password = "wrong-password"
        };

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.False(vm.Succeeded);
        Assert.Equal("اسم المستخدم أو كلمة المرور غير صحيحة", vm.Message);
        Assert.Empty(vm.Password);
    }

    [Fact]
    public async Task CorrectCredentials_PromptsPasswordChangeForFirstTimeAdmin()
    {
        var vm = new LoginViewModel(_fixture.Services)
        {
            Username = "admin",
            Password = "admin"
        };

        await vm.SignInCommand.ExecuteAsync(null);

        // Seeded admin has MustChangePassword = true
        Assert.True(vm.MustChangePassword);
        Assert.NotNull(vm.SignedInUser);
        Assert.Equal("admin", vm.SignedInUser.Username);
        Assert.False(vm.Succeeded); // Must change password first
    }

    [Fact]
    public async Task PasswordStrengthValidation_ValidatesLengthAndMatching()
    {
        var vm = new LoginViewModel(_fixture.Services);

        vm.NewPassword = "123";
        Assert.False(vm.HasValidNewPasswordLength);

        vm.NewPassword = "secret";
        Assert.True(vm.HasValidNewPasswordLength);
        Assert.False(vm.PasswordsMatch);

        vm.ConfirmPassword = "secret";
        Assert.True(vm.PasswordsMatch);
    }

    [Fact]
    public async Task ChangePasswordFlow_UpdatesHashAndCompletesLogin()
    {
        var vm = new LoginViewModel(_fixture.Services)
        {
            Username = "admin",
            Password = "admin"
        };

        await vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.MustChangePassword);

        vm.NewPassword = "new-strong-password";
        vm.ConfirmPassword = "new-strong-password";

        await vm.ChangePasswordCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.False(vm.MustChangePassword);

        // Verify that the new password works in subsequent logins
        var vm2 = new LoginViewModel(_fixture.Services)
        {
            Username = "admin",
            Password = "new-strong-password"
        };
        await vm2.SignInCommand.ExecuteAsync(null);

        Assert.True(vm2.Succeeded);
        Assert.False(vm2.MustChangePassword);
    }

    [Fact]
    public void TogglePasswordVisibility_SwitchesPasswordChars()
    {
        var vm = new LoginViewModel(_fixture.Services);

        Assert.Equal('●', vm.PasswordChar);
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.Equal('\0', vm.PasswordChar);

        Assert.Equal('●', vm.NewPasswordChar);
        vm.ToggleNewPasswordVisibilityCommand.Execute(null);
        Assert.Equal('\0', vm.NewPasswordChar);
    }

    [Fact]
    public async Task Initialize_PopulatesAvailableUsers()
    {
        var vm = new LoginViewModel(_fixture.Services);

        await vm.InitializeCommand.ExecuteAsync(null);

        Assert.Contains("admin", vm.AvailableUsers);
        Assert.Equal("admin", vm.Username);
    }
}

