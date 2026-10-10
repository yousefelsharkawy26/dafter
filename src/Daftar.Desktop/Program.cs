using System;
using System.Threading.Tasks;
using System.IO;
using Avalonia;
using Daftar.Desktop;
using Daftar.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        IServiceProvider? services = null;

        // Registered before anything can throw. Previously InitializeDatabase ran first and outside
        // the try, so a corrupt database or a failed migration killed the process with no window,
        // no dialog and no record — the user could not tell whether their ledger was intact.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("FATAL", "Unhandled exception", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("ERROR", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            services = Bootstrap.BuildServiceProvider();
            var report = Bootstrap.InitializeDatabase(services);

            if (!report.IsClean)
            {
                var detail = report.Completed
                    ? "اتلقى على شبهة في دفاتر الحسابات. شوف ملف السجل."
                    : "فحص سلامة الدفاتر ما اشتغلش — الحسابات متحققةش.";
                StartupError.Show(
                    report.Completed ? "تحذير في الدفاتر" : "تحقق الدفاتر",
                    detail + Environment.NewLine + Environment.NewLine +
                    "التفاصيل: " + report);
            }

            BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FATAL", "Startup failed", ex);

            // No Avalonia window exists yet, so this has to be a plain message box. The user needs
            // to know whether the ledger is safe, not just that "something went wrong".
            StartupError.Show(
                "دفتر مقدرش يشتغل",
                "حصل خطأ أثناء فتح قاعدة البيانات، والتطبيق اتقفل." + Environment.NewLine +
                Environment.NewLine +
                "ما تحذفش أي ملف من مجلد البيانات. لو المشكلة فضلت، شغّل النسخة الاحتياطية" +
                " الموجودة في مجلد Backups." + Environment.NewLine + Environment.NewLine +
                ex.Message);

            return 1;
        }
        finally
        {
            (services as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Diagnostics go to a file next to the database. Console.WriteLine reached nowhere in a GUI
    /// process, which is how a corrupted ledger could start up silently every single time.
    /// </summary>
    private static void Log(string level, string message, Exception? ex)
    {
        try
        {
            var dir = DaftarPaths.LogDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"daftar-{DateTime.Now:yyyy-MM-dd}.log");

            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}" +
                (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine);
        }
        catch
        {
            // Logging must never be what takes the app down.
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider? services = null)
        => AppBuilder
            .Configure(() => new App(services))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            // Avalonia's default font has no Arabic glyphs. Registering Cairo makes the family
            // resolvable, so every control falls back to it for Arabic text.
            .AfterSetup(_ => Fonts.Register())
            .LogToTrace();
}