using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class ReceiveView : UserControl
{
    private bool _loaded;

    public ReceiveView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusSearch();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ReceiveViewModel vm)
            return;

        // The view model cannot reach a file picker on its own, so hand it one.
        vm.PickImage ??= PickImageAsync;

        if (_loaded)
            return;

        _loaded = true;
        await ScreenLoad.Run(vm.LoadAsync, m => vm.Status = m);
        FocusSearch();
    }

    private void FocusSearch()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var searchBox = this.FindControl<Views.Components.SearchBox>("SearchBox");
            searchBox?.FocusSearch();
        });
    }

    private void OnSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is ReceiveViewModel vm)
        {
            vm.SubmitSearchCommand.Execute(null);
            e.Handled = true;
            FocusSearch();
        }
    }

    /// <summary>
    /// Opens the picker and returns a local path to the chosen invoice photo.
    ///
    /// A picked file is not always a plain path: on Linux it can arrive as a document-portal URI,
    /// and <c>TryGetLocalPath</c> then returns null. Tesseract needs a real file, so anything
    /// without a path is copied to a temporary one instead of being dropped silently.
    /// </summary>
    private async Task<string?> PickImageAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window { StorageProvider: { CanOpen: true } provider })
            return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "اختار صورة فاتورة المورد",
            AllowMultiple = false,
            FileTypeFilter =
            [
                FilePickerFileTypes.ImageAll,
                new FilePickerFileType("كل الملفات") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
            return null;

        var file = files[0];

        if (file.TryGetLocalPath() is { Length: > 0 } local && File.Exists(local))
            return local;

        await using var source = await file.OpenReadAsync();
        if (source is null)
            return null;

        var extension = System.IO.Path.GetExtension(file.Name) is { Length: > 0 } ext ? ext : ".png";
        var temporary = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"daftar-scan-{Guid.NewGuid():N}{extension}");

        await using (var target = File.Create(temporary))
            await source.CopyToAsync(target);

        return temporary;
    }
}