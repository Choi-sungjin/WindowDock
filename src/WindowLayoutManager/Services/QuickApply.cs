using WindowLayoutManager.Model;

namespace WindowLayoutManager.Services;

/// <summary>
/// Single source of truth for the saved-layout display order, used by the main list, the tray
/// menu and Previous/Next navigation. Name (current-culture, same as the original list order)
/// then profile ID (ordinal) so same-name ties are stable everywhere.
/// </summary>
public static class ProfileOrdering
{
    public static List<Profile> Sort(IEnumerable<Profile> profiles) =>
        profiles
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Previous/Next target over the full sorted list (not only the 8 slots), wrapping at both
    /// ends. Anchor: the active profile when it is in the list, else the selected profile when it
    /// is in the list, else none (next → first, previous → last). Empty list → null.
    /// </summary>
    public static string? ResolveNeighbor(IReadOnlyList<string> sortedIds, string? activeId, string? selectedId, bool next)
    {
        if (sortedIds.Count == 0) return null;
        int anchor = activeId is null ? -1 : IndexOf(sortedIds, activeId);
        if (anchor < 0 && selectedId is not null) anchor = IndexOf(sortedIds, selectedId);
        if (anchor < 0) return next ? sortedIds[0] : sortedIds[^1];
        int n = sortedIds.Count;
        int idx = next ? (anchor + 1) % n : (anchor - 1 + n) % n;
        return sortedIds[idx];
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.Ordinal)) return i;
        }
        return -1;
    }
}

public enum QuickApplyOutcome
{
    Applied,
    Unassigned,
    ProfileMissing,
    ProfileUnreadable,
    NoProfiles,
    Busy
}

public sealed record QuickApplyResult(
    QuickApplyOutcome Outcome,
    string? ProfileId,
    string? ProfileName,
    IReadOnlyList<PlaceReport> Reports,
    string? Detail)
{
    public static QuickApplyResult Of(QuickApplyOutcome outcome, string? profileId = null, string? detail = null) =>
        new(outcome, profileId, null, Array.Empty<PlaceReport>(), detail);
}

/// <summary>
/// Explicit quick-apply (slot button, slot hotkey, Previous/Next). Uses the SAME real apply
/// semantics as the main Apply button: set active, apply only already-open matching windows,
/// enable+persist monitoring, return actual per-role reports. Never launches programs.
/// The target is always re-loaded from disk by stable ID at invocation time. A missing,
/// unreadable, corrupt or ID-mismatched profile returns early BEFORE SetActiveProfile, so it can
/// never cause a different profile to be applied.
/// </summary>
public static class QuickApply
{
    public static QuickApplyResult ApplyById(ProfileStore store, MonitoringService monitor, string? profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return QuickApplyResult.Of(QuickApplyOutcome.Unassigned);
        if (!ProfileStore.IsSafeId(profileId)) return QuickApplyResult.Of(QuickApplyOutcome.ProfileUnreadable, null, "unsafe_id");

        Profile? p;
        try
        {
            p = store.Load(profileId);
        }
        catch (Exception ex)
        {
            return QuickApplyResult.Of(QuickApplyOutcome.ProfileUnreadable, profileId, SafeMessage(ex));
        }
        if (p is null) return QuickApplyResult.Of(QuickApplyOutcome.ProfileMissing, profileId);
        if (!string.Equals(p.Id, profileId, StringComparison.Ordinal))
            return QuickApplyResult.Of(QuickApplyOutcome.ProfileUnreadable, profileId, "id_mismatch");

        monitor.SetActiveProfile(p);
        var reports = monitor.ApplyActiveProfile();
        // Detail carries a settings persistence error (monitoring is still on for this session).
        return new QuickApplyResult(QuickApplyOutcome.Applied, p.Id, p.Name, reports, monitor.LastPersistError);
    }

    /// <summary>IO exception messages can contain full user paths; report only the type for those.</summary>
    internal static string SafeMessage(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ? ex.GetType().Name : ex.Message;
}
