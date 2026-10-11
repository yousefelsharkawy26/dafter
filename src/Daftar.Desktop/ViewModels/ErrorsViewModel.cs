using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

/// <summary>
/// Shows the app's own faults, as recorded in the log files.
///
/// <para>
/// It reads the files rather than an in-memory list so that a failure reported days later is still
/// here, and so that Entity Framework's own diagnostics — which never pass through
/// <see cref="FaultLog"/> and which explain a fair share of these failures — show up alongside
/// ours.
/// </para>
/// </summary>
public partial class ErrorsViewModel : ObservableObject
{
    public ErrorsViewModel()
    {
        // Computed off the collection, so the count and the headline have to be told when it
        // changes. Watching the collection is the only place that can happen, and doing it here
        // means neither the loader nor anything else has to remember.
        Entries.CollectionChanged += (_, _) => NotifyChanged();
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>The row being read. Its detail is what names the cause.</summary>
    [ObservableProperty] private LogEntry? _selected;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Where the files live, so the answer can be handed to whoever is looking.</summary>
    public string LogDirectoryText => DaftarPaths.LogDirectory;

    public bool HasEntries => Entries.Count > 0;

    public bool HasSelection => Selected is not null;

    /// <summary>Headline: how bad, and how recent. The rest is only worth reading if this is.</summary>
    public string SummaryText => Entries.Count == 0
        ? "مفيش أخطاء مسجّلة."
        : $"{Entries.Count} خطأ في آخر ١٤ يوم — الأحدث: {Entries[0].TimeText}";

    partial void OnSelectedChanged(LogEntry? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>Public so the view can fill the screen the moment it opens.</summary>
    public Task InitializeAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await Task.Run(() => LogReader.Read(DaftarPaths.LogDirectory));

            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(entry);

            // A selection pointing at a row that is no longer there would show a detail pane for
            // something the user cannot see selected.
            if (Selected is not null && !Entries.Contains(Selected))
                Selected = Entries.FirstOrDefault();

            StatusMessage = Entries.Count > 0
                ? $"اتقر على أي سطر عشان تشوف تفاصيله."
                : "مفيش أخطاء. لو المشكلة بتتكرر، دوّر على صلاحيات الكتابة على مجلد البيانات.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(SummaryText));
    }
}