namespace StreakCelebration.Models;

/// <summary>How big a celebration is. Chosen by the caller; the overlay never derives it from the count.</summary>
public enum CelebrationLevel
{
    /// <summary>Small, subtle animation (e.g. a streak was extended).</summary>
    Normal,

    /// <summary>Larger animation with glow and particles (e.g. a configured milestone).</summary>
    Milestone,

    /// <summary>Most intense celebration (e.g. a new personal best).</summary>
    Record,
}

/// <summary>Vertical placement of the celebration content.</summary>
public enum CelebrationPosition
{
    /// <summary>Centered on the screen.</summary>
    Center,

    /// <summary>Near the top edge (below the safe area).</summary>
    Top,

    /// <summary>Near the bottom edge (above the safe area).</summary>
    Bottom,
}

/// <summary>How a celebration request ended. Cancellation through the caller's token is reported as a cancelled task instead.</summary>
public enum StreakCelebrationResult
{
    /// <summary>The full animation was shown, held and faded out.</summary>
    Completed,

    /// <summary>The user tapped the overlay, or <see cref="Services.IStreakCelebrationService.DismissCurrent"/> was called.</summary>
    Dismissed,

    /// <summary>The overlay started but its host went away (page unloaded, app moved to the background) or the service was disposed.</summary>
    Interrupted,

    /// <summary>The request waited longer than <see cref="StreakCelebrationOptions.QueuedLifetime"/> and was never shown.</summary>
    Expired,

    /// <summary>The request was removed without being shown: queue overflow, <see cref="Services.IStreakCelebrationService.ClearQueue"/> or service disposal.</summary>
    Dropped,
}
