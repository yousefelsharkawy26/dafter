using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Daftar;

/// <summary>
/// Registers the bundled Cairo font, which carries the Arabic glyphs.
///
/// Avalonia's default font (Inter) has no Arabic coverage, so without this the whole shell renders
/// as blank boxes. Bundling the font also keeps the app looking the same on any machine instead of
/// depending on Cairo happening to be installed.
/// </summary>
internal static class Fonts
{
    private const string Resource = "avares://Daftar.Desktop/Assets/Fonts/Cairo.ttf";

    public static FontFamily Family { get; } = new($"{Resource}#Cairo");

    /// <summary>
    /// The key identifies the collection; the source is the font file itself. The family name is
    /// taken from the font's own metadata, which is why "Cairo" resolves everywhere afterwards.
    /// </summary>
    public static void Register()
    {
        var manager = FontManager.Current;

        manager.AddFontCollection(new EmbeddedFontCollection(
            key: new Uri($"fonts:{Family.Name}"),
            source: new Uri(Resource)));
    }
}