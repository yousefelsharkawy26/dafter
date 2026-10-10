using System;

namespace Daftar.Services;

public interface INavigationService
{
    void Navigate(string key);
    event Action<string>? Navigated;
}

public sealed class NavigationService : INavigationService
{
    public event Action<string>? Navigated;

    public void Navigate(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            Navigated?.Invoke(key);
        }
    }
}

