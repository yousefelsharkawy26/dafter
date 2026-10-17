namespace Daftar;

/// <summary>
/// Arabic counted nouns: the noun agrees with the number in front of it, and this is where that
/// agreement is decided.
///
/// <para>
/// Arabic distinguishes more than one range. From three to ten the counted noun is plural
/// (جولات); at exactly two it is dual (جولتان); at zero, at one and from eleven upwards it is
/// singular (جولة). The app was written as though the singular covered every number, so the
/// session banner read "5 جولة" and the overdue tile read "0 متأخرة" — a plural adjective with no
/// noun left after a zero.
/// </para>
///
/// <para>
/// The forms passed in are whole phrases rather than bare nouns, because the adjective agrees with
/// the number too. "فاتورة|فاتورتان|فواتير" would have produced "5 فواتير متأخرة" but also "2
/// فاتورتان متأخرة"; passing "فواتير متأخرة" as the plural form lets the adjective move with the
/// noun, which is the only way the caller does not have to know the gender rules.
/// </para>
///
/// <para>
/// The number is always written out, in front of the dual as much as anywhere else. "2 جولتان"
/// says two twice; leaving the number off would break a row of counts that are read as a column,
/// and the redundancy costs nothing.
/// </para>
/// </summary>
public static class ArabicText
{
    /// <summary>
    /// <paramref name="count"/>, followed by the form of the word that agrees with it.
    ///
    /// <para>
    /// <paramref name="singular"/> covers zero, one, and eleven and above — the ranges Arabic
    /// leaves singular. Exactly two takes the dual, and three to ten the plural. No caller has to
    /// decide anything per number, and no caller has to know which range it is in: one call covers
    /// "0 جولة", "1 جولة", "2 جولتان", "5 جولات" and "15 جولة".
    /// </para>
    /// </summary>
    /// <param name="count">The number being counted. Never negative; counts come from collections.</param>
    /// <param name="singular">The form for zero, one and eleven and up: "جولة".</param>
    /// <param name="dual">The form for exactly two: "جولتان".</param>
    /// <param name="plural">The form for three to ten: "جولات".</param>
    public static string Count(long count, string singular, string dual, string plural)
        => $"{count} {Noun(count, singular, dual, plural)}";

    /// <summary>
    /// The form on its own, with no number in front of it.
    ///
    /// <para>
    /// This is for the few places that print the number in a size of its own and set the word
    /// beside it smaller, which is why the start screen's low-stock tile printed "أصناف" as a
    /// literal and read "1 أصناف" at one product. Binding the whole phrase there would print the
    /// number twice; binding the word alone lets it move with the number beside it.
    /// </para>
    /// </summary>
    /// <param name="count">The number being counted.</param>
    /// <param name="singular">The form for zero, one and eleven and up: "صنف".</param>
    /// <param name="dual">The form for exactly two: "صنفان".</param>
    /// <param name="plural">The form for three to ten: "أصناف".</param>
    public static string Noun(long count, string singular, string dual, string plural)
    {
        if (count == 2)
            return dual;

        if (count is >= 3 and <= 10)
            return plural;

        return singular;
    }
}
