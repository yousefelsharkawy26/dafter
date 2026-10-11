using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Daftar.Converters;

/// <summary>
/// Renders a check mark or a hollow circle, so a validation line can say whether it passed.
///
/// <para>
/// This exists because the login window was written as
/// <c>Text="{Binding PasswordsMatch, Converter={x:Null}, FallbackValue='○'}"</c>, which does not
/// do what it looks like. A null converter is not a conversion, so Avalonia falls back to
/// <c>ToString()</c> and the screen showed the word <c>False</c> where an icon belonged.
/// <c>FallbackValue</c> only applies when the binding fails outright, not when the value
/// converts badly, so it never got a chance.
/// </para>
/// </summary>
public sealed class BoolToMarkConverter : IValueConverter
{
    /// <summary>The circle shown when the condition does not hold.</summary>
    public string WhenFalse { get; set; } = "○";

    /// <summary>The tick shown when it does.</summary>
    public string WhenTrue { get; set; } = "✓";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? WhenTrue : WhenFalse;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The same idea for colour: green once the condition holds, grey while it does not.
///
/// <para>
/// The <see cref="BoolToMarkConverter"/> note applies here too. Binding a <c>bool</c> straight to
/// a <c>Foreground</c> does fail, so the fallback colour happened to be used -- but by way of an
/// error rather than by design, and the two states were indistinguishable.
/// </para>
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly IBrush Met = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
    private static readonly IBrush Unmet = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>The quieter colour, for hint text that should recede until the condition holds.</summary>
    public bool Dim { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Met : Dim ? Unmet : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
