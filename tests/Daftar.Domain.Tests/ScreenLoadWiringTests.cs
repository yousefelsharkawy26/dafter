using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A screen with a status line reports a load failure there; a screen without one shows a dialog.
///
/// <para>
/// This was got wrong twice. First the wiring was copied onto ViewModels that have no
/// <c>Status</c>, which does not compile. Then it was stripped off the ones that do, leaving nine
/// screens to shout in a modal instead of writing a line. A text search for the word cannot tell
/// the two cases apart — a comment mentioning <c>Status</c> reads the same as a property — so this
/// asks the compiler instead, by compiling.
/// </para>
/// </summary>
public sealed class ScreenLoadWiringTests
{
    private static readonly string Views =
        Path.Combine(FindRepoRoot(), "src", "Daftar.Desktop", "Views");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Daftar.Desktop", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string RepoFile(params string[] parts) =>
        Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());

    [Fact]
    public void Every_Screen_Routes_Its_Load_Through_ScreenLoad()
    {
        // The point of the exercise. An async void load call is what took the process down, so no
        // screen may have one any more.
        var offenders = Directory.GetFiles(Views, "*.axaml.cs")
            .Where(f => File.ReadAllText(f).Contains("async void OnDataContextChanged"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f),
                @"await \w+\.(Load|Initialize)Async\(\);"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_Screen_Reports_To_Its_Status_Line_Exactly_When_Its_ViewModel_Has_One()
    {
        var vmSources = Directory.GetFiles(
                RepoFile("src", "Daftar.Desktop", "ViewModels"), "*.cs")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, File.ReadAllText);

        var wrong = new List<string>();

        foreach (var file in Directory.GetFiles(Views, "*.axaml.cs"))
        {
            var src = File.ReadAllText(file);

            var call = Regex.Match(src, @"ScreenLoad\.Run\(\w+\.(\w+)(, m => \w+\.Status = m)?\);");
            if (!call.Success)
                continue;

            var vmType = Regex.Match(src, @"DataContext is not (\w+ViewModel)").Groups[1].Value;
            if (vmType.Length == 0)
                continue;

            // A property, not a mention: the word alone lies.
            var hasProperty = Regex.IsMatch(
                vmSources.GetValueOrDefault(vmType) ?? "",
                @"public[^\n]*\bStatus\b|\[ObservableProperty\][\s\S]{0,200}?private[^\n]*\b_?status\b",
                RegexOptions.IgnoreCase);

            var usesStatusLine = call.Groups[2].Success;

            if (hasProperty != usesStatusLine)
                wrong.Add($"{Path.GetFileName(file)}: {vmType} has={(hasProperty ? "a status line" : "none")}, wired={(usesStatusLine ? "status line" : "dialog")}");
        }

        Assert.Empty(wrong);
    }
}
