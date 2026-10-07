using System;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.Views;

/// <summary>
/// The page area. A tab either has a dedicated view, or renders a placeholder describing what the
/// page will do.
///
/// The view model type comes from the tab rather than being derived by reflection: a name-based
/// lookup that fails leaves every binding on the page silently null, with no error anywhere.
/// </summary>
public sealed class TabHost : ContentControl
{
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not ViewModels.TabViewModel tab)
            return;

        // Rebuild only when the page actually changes, so switching tabs keeps a half-typed form.
        if (Content is Control existing && existing.Tag as string == tab.Key)
            return;

        Content = BuildView(tab);
    }

    private Control BuildView(ViewModels.TabViewModel tab)
    {
        if (tab.ViewType is null)
        {
            return new PlaceholderView { Tag = tab.Key, DataContext = tab };
        }

        if (Activator.CreateInstance(tab.ViewType) is not Control view)
            return new PlaceholderView { Tag = tab.Key, DataContext = tab };

        view.Tag = tab.Key;

        if (tab.ViewModelType is not null)
            view.DataContext = App.Services?.GetService(tab.ViewModelType);

        return view;
    }
}