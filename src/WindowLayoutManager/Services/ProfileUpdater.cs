using WindowLayoutManager.Model;

namespace WindowLayoutManager.Services;

public enum UpdateAction
{
    WouldUpdate,
    PreserveNoMatch,
    PreserveAmbiguous
}

public sealed record EntryUpdate(string EntryId, string Role, UpdateAction Action, SavedRect OldRect, bool OldMaximized, SavedRect NewRect, bool NewMaximized);

public sealed class UpdateProposal
{
    /// <summary>Detached clone of the profile with proposed changes applied. The original profile
    /// instance is NOT modified by Build; the caller commits by saving this clone and copying its
    /// contents back into the live model only after a successful persist.</summary>
    public required Profile Proposed { get; init; }
    public required IReadOnlyList<EntryUpdate> Updates { get; init; }
    public int UpdatedCount => Updates.Count(u => u.Action == UpdateAction.WouldUpdate);
    public int PreservedNoMatchCount => Updates.Count(u => u.Action == UpdateAction.PreserveNoMatch);
    public int PreservedAmbiguousCount => Updates.Count(u => u.Action == UpdateAction.PreserveAmbiguous);
    public bool HasAnyChange => UpdatedCount > 0;
}

public static class ProfileUpdater
{
    /// <summary>
    /// Builds a transactional update proposal for refreshing an existing profile's entry geometry
    /// from the current window snapshot. The ORIGINAL profile instance is never mutated by this
    /// method; callers must save the returned <see cref="UpdateProposal.Proposed"/> and only copy its
    /// contents back after persistence succeeds. Entries whose current window is ambiguous or absent
    /// are preserved unchanged. Custom role names and title filters are preserved for all entries.
    /// </summary>
    public static UpdateProposal Build(Profile original, IReadOnlyList<WindowInfo> windows)
    {
        var cloned = CloneProfile(original);
        var matches = LayoutEngine.MatchProfile(cloned, windows);
        var updates = new List<EntryUpdate>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var orig = original.Entries[i];
            if (m.Outcome == MatchOutcome.Matched && m.Window is not null)
            {
                var newRectSrc = m.Window.ShowState == WindowShowState.Normal ? m.Window.CurrentRect : m.Window.NormalRect;
                var newRect = SavedRect.From(newRectSrc);
                var newMax = m.Window.ShowState == WindowShowState.Maximized;
                cloned.Entries[i].Rect = newRect;
                cloned.Entries[i].Maximized = newMax;
                updates.Add(new EntryUpdate(orig.Id, orig.Role, UpdateAction.WouldUpdate,
                    SnapRect(orig.Rect), orig.Maximized, newRect, newMax));
            }
            else if (m.Outcome == MatchOutcome.Ambiguous)
            {
                updates.Add(new EntryUpdate(orig.Id, orig.Role, UpdateAction.PreserveAmbiguous,
                    SnapRect(orig.Rect), orig.Maximized, SnapRect(orig.Rect), orig.Maximized));
            }
            else
            {
                updates.Add(new EntryUpdate(orig.Id, orig.Role, UpdateAction.PreserveNoMatch,
                    SnapRect(orig.Rect), orig.Maximized, SnapRect(orig.Rect), orig.Maximized));
            }
        }
        return new UpdateProposal { Proposed = cloned, Updates = updates };
    }

    public static Profile CloneProfile(Profile src)
    {
        var copy = new Profile
        {
            Id = src.Id,
            Name = src.Name,
            Version = src.Version,
            CreatedUtc = src.CreatedUtc,
            UpdatedUtc = src.UpdatedUtc,
            Entries = src.Entries.Select(e => new ProfileEntry
            {
                Id = e.Id,
                Role = e.Role,
                ExePath = e.ExePath,
                ProcessName = e.ProcessName,
                ClassName = e.ClassName,
                TitleContains = e.TitleContains,
                Rect = new SavedRect { X = e.Rect.X, Y = e.Rect.Y, Width = e.Rect.Width, Height = e.Rect.Height },
                Maximized = e.Maximized,
            }).ToList()
        };
        return copy;
    }

    public static void CopyInto(Profile dest, Profile source)
    {
        // Copy fields; keep the SAME dest object identity so UI references remain valid.
        dest.Name = source.Name;
        dest.Version = source.Version;
        dest.UpdatedUtc = source.UpdatedUtc;
        dest.Entries.Clear();
        foreach (var e in source.Entries)
        {
            dest.Entries.Add(new ProfileEntry
            {
                Id = e.Id,
                Role = e.Role,
                ExePath = e.ExePath,
                ProcessName = e.ProcessName,
                ClassName = e.ClassName,
                TitleContains = e.TitleContains,
                Rect = new SavedRect { X = e.Rect.X, Y = e.Rect.Y, Width = e.Rect.Width, Height = e.Rect.Height },
                Maximized = e.Maximized,
            });
        }
    }

    private static SavedRect SnapRect(SavedRect r) =>
        new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
}
