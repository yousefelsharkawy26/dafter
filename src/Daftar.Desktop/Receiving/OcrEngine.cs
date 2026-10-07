using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Daftar.Desktop.Receiving;

/// <summary>Why a scan could not be produced, so the UI can say something actionable.</summary>
public enum OcrFailure
{
    None,
    EngineMissing,
    ArabicDataMissing,
    BadImage,
    TimedOut,
    EngineError,
}

public sealed record OcrResult(string Text, OcrFailure Failure, string? Detail)
{
    public bool Ok => Failure == OcrFailure.None;
}

/// <summary>
/// Reads an invoice image into text with Tesseract, then hands it to <see cref="InvoiceParser"/>.
///
/// Tesseract is a separate program, not a library, so it is found at run time rather than bundled:
/// an engine the user cannot update is worse than one they can. If it is missing the scan reports
/// why instead of failing silently, and the manager can still type the lines in.
/// </summary>
public sealed class OcrEngine
{
    /// <summary>Seconds before a scan is abandoned; a photo of one page takes a couple of seconds.</summary>
    private const int TimeoutSeconds = 60;

    private readonly string? _enginePath;
    private readonly string? _tessdataPath;

    public OcrEngine()
        : this(ResolveEnginePath(), ResolveTessdataPath())
    {
    }

    public OcrEngine(string? enginePath, string? tessdataPath)
    {
        _enginePath = enginePath;
        _tessdataPath = tessdataPath;
    }

    /// <summary>Whether OCR is usable on this machine right now.</summary>
    public bool IsAvailable => _enginePath is not null && File.Exists(_enginePath);

    /// <summary>
    /// Reads the image and parses it. Never throws: a failure comes back as an <see cref="OcrFailure"/>
    /// the UI can explain.
    /// </summary>
    public async Task<(ScannedInvoice Invoice, OcrResult Result)> ScanAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return (ScannedInvoice.Empty, new OcrResult(
                string.Empty,
                OcrFailure.EngineMissing,
                "Tesseract is not installed. Install it with: sudo apt install tesseract-ocr tesseract-ocr-ara"));
        }

        if (!File.Exists(imagePath))
            return (ScannedInvoice.Empty, new OcrResult(string.Empty, OcrFailure.BadImage, imagePath));

        var outputBase = Path.Combine(Path.GetTempPath(), "daftar-ocr-" + Guid.NewGuid().ToString("N"));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _enginePath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Arabic plus English: invoices mix both, and the digits must be read as digits.
            foreach (var argument in BuildArguments(imagePath, outputBase))
                startInfo.ArgumentList.Add(argument);

            // A self-contained install keeps libtesseract beside the executable rather than
            // on the system loader path, so point the loader at it.
            ApplyLocalLibraryPath(startInfo);

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return (ScannedInvoice.Empty, new OcrResult(
                    string.Empty, OcrFailure.EngineMissing, ex.Message));
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return (ScannedInvoice.Empty, new OcrResult(
                    string.Empty, OcrFailure.TimedOut, $"{TimeoutSeconds}s"));
            }

            var textFile = outputBase + ".txt";
            var text = File.Exists(textFile) ? await File.ReadAllTextAsync(textFile, ct) : string.Empty;

            TryDelete(textFile);

            if (process.ExitCode != 0)
            {
                var error = await stderrTask;
                var failure = error.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase)
                    ? OcrFailure.ArabicDataMissing
                    : OcrFailure.EngineError;

                return (ScannedInvoice.Empty, new OcrResult(string.Empty, failure, error.Trim()));
            }

            if (string.IsNullOrWhiteSpace(text))
                return (ScannedInvoice.Empty, new OcrResult(string.Empty, OcrFailure.BadImage, "no text found"));

            await stdoutTask;
            return (InvoiceParser.Parse(text), new OcrResult(text, OcrFailure.None, null));
        }
        finally
        {
            TryDelete(outputBase + ".txt");
        }
    }

    /// <summary>
    /// Adds a <c>lib</c> directory sitting next to the engine to the loader search path, so a
    /// copied-in engine runs without a system install. Does nothing on Windows, where the DLLs
    /// are found next to the .exe already.
    /// </summary>
    private void ApplyLocalLibraryPath(ProcessStartInfo startInfo)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _enginePath is null)
            return;

        var libDirectory = Path.Combine(Path.GetDirectoryName(_enginePath)!, "lib");
        if (!Directory.Exists(libDirectory))
            return;

        var key = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "DYLD_LIBRARY_PATH"
            : "LD_LIBRARY_PATH";
        startInfo.Environment.TryGetValue(key, out var existing);

        startInfo.Environment[key] = string.IsNullOrWhiteSpace(existing)
            ? libDirectory
            : libDirectory + Path.PathSeparator + existing;
    }

    private string[] BuildArguments(string imagePath, string outputBase)
    {
        var args = new System.Collections.Generic.List<string>
        {
            imagePath,
            outputBase,
            "-l",
            "ara+eng",
        };

        if (_tessdataPath is not null)
        {
            args.Add("--tessdata-dir");
            args.Add(_tessdataPath);
        }

        // Page 1 only: an invoice is one page, and skipping the rest is much faster.
        args.Add("--psm");
        args.Add("6");

        return args.ToArray();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Nothing useful to do if the process already went.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stray temp file is not worth surfacing to the manager.
        }
    }

    /// <summary>
    /// Looks for the engine in the order a user would expect: an explicit override, the app's own
    /// data directory (where the bundled install script puts it), then the PATH.
    /// </summary>
    private static string? ResolveEnginePath()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tesseract.exe" : "tesseract";

        var overridePath = Environment.GetEnvironmentVariable("DAFTAR_TESSERACT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var directory in SearchDirectories())
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? ResolveTessdataPath()
    {
        foreach (var directory in SearchDirectories())
        {
            // tessdata lives one level under a prefix, e.g. <prefix>/share/tesseract-ocr/5/tessdata
            foreach (var relative in new[]
                     {
                         Path.Combine("share", "tesseract-ocr", "5", "tessdata"),
                         Path.Combine("share", "tessdata"),
                         "tessdata",
                     })
            {
                var candidate = Path.Combine(directory, relative);
                if (System.IO.Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string[] SearchDirectories()
    {
        var found = new System.Collections.Generic.List<string>
        {
            // Where the install script drops a self-contained copy.
            Path.Combine(Daftar.DaftarPaths.AppDataDirectory, "ocr"),
            Path.Combine(AppContext.BaseDirectory, "ocr"),
        };

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                found.Add(entry.Trim('"'));
        }

        return found.ToArray();
    }
}