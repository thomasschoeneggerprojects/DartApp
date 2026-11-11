namespace StreakCelebration.Models;

/// <summary>
/// What to celebrate. The caller supplies already-localized text and the <see cref="Level"/>; the overlay only displays it.
/// </summary>
public sealed record StreakCelebrationData
{
    /// <summary>The streak count shown in large digits (0 or more).</summary>
    public int Count { get; init; }

    /// <summary>Headline, e.g. "10 STREAK!". Required.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Optional second line, e.g. "UNSTOPPABLE!".</summary>
    public string? Message { get; init; }

    /// <summary>Celebration size. Defaults to <see cref="CelebrationLevel.Normal"/>.</summary>
    public CelebrationLevel Level { get; init; } = CelebrationLevel.Normal;

    /// <summary>Optional icon for this celebration only; overrides <see cref="StreakCelebrationOptions.IconSource"/>.</summary>
    public string? IconSource { get; init; }

    /// <summary>Text read by screen readers. Defaults to title and message.</summary>
    public string? AccessibilityText { get; init; }

    /// <summary>The text announced to screen readers.</summary>
    public string Announcement => AccessibilityText
        ?? (string.IsNullOrWhiteSpace(Message) ? Title : $"{Title} {Message}");

    /// <summary>Returns all validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Count < 0) errors.Add($"{nameof(Count)} must not be negative.");
        if (string.IsNullOrWhiteSpace(Title)) errors.Add($"{nameof(Title)} is required.");
        if (!Enum.IsDefined(Level)) errors.Add($"{nameof(Level)} '{Level}' is not a defined {nameof(CelebrationLevel)}.");
        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <see cref="Validate"/> reports errors.</summary>
    public void ThrowIfInvalid()
    {
        IReadOnlyList<string> errors = Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid celebration data: " + string.Join(" ", errors));
    }
}
