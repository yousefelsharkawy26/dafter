using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Daftar.Converters;

/// <summary>
/// Turns a count into the phrase that agrees with it, for the few screens that write a phrase
/// straight into a binding instead of building it in a view model.
///
/// <para>
/// The parameter carries the three forms the word takes, separated by <c>|</c> —
/// <c>ConverterParameter='صنف|صنفان|أصناف'</c>. Which range applies is
/// <see cref="ArabicText.Count"/>'s business and is not decided again here; this only splits the
/// parameter and hands the forms over.
/// </para>
///
/// <para>
/// A parameter that is not three forms, or a value that is not a number, leaves the raw value on
/// screen rather than blanking it. A number with the wrong noun on it is a mistake; an empty space
/// where a number should be is a broken screen, and this runs while the window is being laid out,
/// where there is nobody to read an exception.
/// </para>
/// </summary>
public sealed class ArabicCountConverter : IValueConverter
{
    private const char Separator = '|';

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not null
            && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && parameter is string forms)
        {
            var parts = forms.Split(Separator);

            if (parts.Length == 3 && parts.All(part => part.Length > 0))
                return ArabicText.Count(count, parts[0], parts[1], parts[2]);
        }

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The same three forms, but the word alone: for the few tiles that print the number in a size of
/// its own and set the noun beside it smaller. Binding the whole phrase there would print the
/// number twice.
///
/// <para>
/// The parameter and the failure behaviour are <see cref="ArabicCountConverter"/>'s — three forms
/// separated by <c>|</c>, and anything unreadable left on screen rather than blanked. The start
/// screen's low-stock figure used this word as a literal, so it read "1 أصناف" at one product and
/// could not move with the number at all.
/// </para>
/// </summary>
public sealed class ArabicNounConverter : IValueConverter
{
    private const char Separator = '|';

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not null
            && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && parameter is string forms)
        {
            var parts = forms.Split(Separator);

            if (parts.Length == 3 && parts.All(part => part.Length > 0))
                return ArabicText.Noun(count, parts[0], parts[1], parts[2]);
        }

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
