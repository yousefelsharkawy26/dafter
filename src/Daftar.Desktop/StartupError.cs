using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Daftar.Desktop;

/// <summary>
/// A message box for the window before Avalonia's lifetime starts.
///
/// <see cref="Program.Main"/> registers crash handlers and opens the database before any window
/// exists, so a corrupt ledger used to end the process with nothing on screen at all. The owner
/// of a food-distribution shop needs to know their books are safe, and where the backups are —
/// silence is the worst possible answer here.
/// </summary>
internal static class StartupError
{
    public static void Show(string title, string message)
    {
        // Startup can be mid-UI-thread or not; posting keeps it legal either way.
        if (Dispatcher.UIThread.CheckAccess())
            ShowCore(title, message);
        else
            Dispatcher.UIThread.Post(() => ShowCore(title, message));
    }

    private static void ShowCore(string title, string message)
    {
        try
        {
            var window = new Window
            {
                Title = title,
                Width = 520,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CanResize = false,
            };

            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Margin = new Thickness(20),
            };

            var close = new Button
            {
                Content = "تمام",
                Width = 100,
                Height = 34,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Margin = new Thickness(20, 0, 20, 16),
            };
            close.Click += (_, _) => window.Close();

            var panel = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            panel.Children.Add(close);
            panel.Children.Add(text);

            window.Content = panel;
            window.Show();
        }
        catch
        {
            // If even this fails there is nothing left to try but the log file.
        }
    }
}