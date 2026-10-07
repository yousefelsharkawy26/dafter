using Daftar.Domain.Shared;

namespace Daftar.Domain.Shared;

/// <summary>
/// Allocates document numbers. The next number is taken inside the posting transaction, so a
/// failed post rolls the counter back with it: no gaps, and drafts stay unnumbered.
/// Year 0 means the sequence never resets.
/// </summary>
public sealed class DocumentSequence : IDomainValidatable
{
    public int Id { get; set; }
    public DocumentType DocType { get; set; }
    public int Year { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public int NextNumber { get; set; } = 1;
    public int Padding { get; set; } = 6;

    /// <summary>
    /// Formats the current number and advances the sequence. <paramref name="year"/> is informational
    /// only — Year is never rewritten here, because a Year = 0 row means "never resets" and stamping
    /// the current year onto it would turn it into an ordinary annual sequence.
    /// </summary>
    public string Take(int year)
    {
        var number = NextNumber;
        NextNumber++;
        return Format(number);
    }

    public string Format(int number)
        => $"{Prefix}{number.ToString().PadLeft(Padding, '0')}";

    public void Validate()
    {
        if (NextNumber < 1)
            throw new DomainException($"Sequence {DocType}/{Year} has NextNumber {NextNumber}; must be >= 1.");
        if (Padding < 1)
            throw new DomainException($"Sequence {DocType}/{Year} has Padding {Padding}; must be >= 1.");
    }
}