using WindowLayoutManager.Model;
using WindowLayoutManager.Native;

namespace WindowLayoutManager.Services;

public interface IWindowProvider
{
    EnumerationResult Enumerate();
    bool IsAlive(WindowIdentity id);
    /// <summary>All top-level window identities currently alive (including hidden/cloaked). Used
    /// to prime "seen" set at startup/resume so hidden-at-start windows are not later treated as new.</summary>
    IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities();
    /// <summary>All top-level windows with FULL metadata (class, title, exe, placement) including
    /// hidden/cloaked ones. Used at cold startup to reserve profile slots for identifiable hidden
    /// windows (no movement). Shell/system classes are still filtered.</summary>
    IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata();
    MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string expectedExeKey, string? expectedClassName = null);
}

public sealed class ProductionWindowProvider : IWindowProvider
{
    private readonly WindowEnumerator _enumerator = new();
    private readonly WindowMover _mover = new();
    private readonly int _ownPid;

    public ProductionWindowProvider(int ownPid) { _ownPid = ownPid; }

    public EnumerationResult Enumerate() => _enumerator.Enumerate(_ownPid);
    public bool IsAlive(WindowIdentity id) => WindowEnumerator.IsAliveNative(id);
    public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => _enumerator.EnumerateAnyTopLevelIdentities(_ownPid);
    public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata() => _enumerator.EnumerateAnyTopLevelWithMetadata(_ownPid);
    public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string expectedExeKey, string? expectedClassName = null)
        => _mover.Move(id, target, maximize, expectedExeKey, expectedClassName);
}

public enum MatchOutcome
{
    Matched,
    NoMatch,
    Ambiguous,
    SlotOccupied,
    IdentityStale
}

public sealed class EntryMatch
{
    public required ProfileEntry Entry { get; init; }
    public required MatchOutcome Outcome { get; init; }
    public WindowInfo? Window { get; init; }
    public IReadOnlyList<WindowInfo> Candidates { get; init; } = Array.Empty<WindowInfo>();
    public string? Note { get; init; }
}

public static class LayoutEngine
{
    public static List<EntryMatch> MatchProfile(Profile profile, IReadOnlyList<WindowInfo> windows)
        => MatchProfile(profile, windows, occupiedSlots: new Dictionary<string, WindowIdentity>());

    /// <summary>
    /// Strict-predicate bipartite matching with TRUE maximum-matching + forced-edge detection.
    /// An unreserved entry is Matched iff its edge appears in EVERY maximum matching (removing it
    /// strictly reduces matching cardinality); otherwise it is Ambiguous. NoMatch means zero strict
    /// candidates. Explicit `occupiedSlots` reservations lock their pair (never broken).
    ///
    /// IMPORTANT: there is NO specificity / greedy / unique-candidate heuristic. In the shortage
    /// graph specific→{A}, general1→{A,B}, general2→{A,B}, the maximum cardinality is 2 and both
    /// {specific:A,general1:B} and {general1:A,general2:B} are valid maxima — therefore all three
    /// roles are Ambiguous. Any disjoint role→{D} with a unique window remains Matched because
    /// removing its edge actually drops cardinality.
    /// </summary>
    public static List<EntryMatch> MatchProfile(Profile profile, IReadOnlyList<WindowInfo> windows, IReadOnlyDictionary<string, WindowIdentity> occupiedSlots)
    {
        var entries = profile.Entries;
        var winById = windows.ToDictionary(w => w.Identity);

        // 1) Build strict candidates per entry.
        var cands = new Dictionary<string, List<WindowInfo>>(StringComparer.Ordinal);
        foreach (var e in entries) cands[e.Id] = StrictCandidatesFor(e, windows);

        // 2) Apply reservations: lock reserved pair (if still strictly matches). Remove the reserved
        //    window from EVERY OTHER entry's candidate pool so the matcher never breaks the lock.
        var locked = new Dictionary<string, WindowInfo>(StringComparer.Ordinal);
        var reservedWindowIds = new HashSet<WindowIdentity>();
        foreach (var e in entries)
        {
            if (!occupiedSlots.TryGetValue(e.Id, out var rId)) continue;
            if (!winById.TryGetValue(rId, out var win)) continue;
            if (!StrictMatches(e, win)) continue;
            locked[e.Id] = win;
            reservedWindowIds.Add(rId);
        }
        foreach (var kv in cands)
        {
            if (locked.ContainsKey(kv.Key)) continue;
            kv.Value.RemoveAll(w => reservedWindowIds.Contains(w.Identity));
        }

        // 3) Compute max matching on free (unreserved) entries. NO constraint propagation / NO
        //    specificity tie-break: forced-edge detection on the true max matching is the only
        //    legal source of a Matched outcome for free entries.
        var freeEntryIds = entries.Where(e => !locked.ContainsKey(e.Id)).Select(e => e.Id).ToList();
        var match = MaxMatching(freeEntryIds, cands);
        int matchSize = match.Count(kv => kv.Value is not null);

        var results = new Dictionary<string, EntryMatch>(StringComparer.Ordinal);
        foreach (var kv in locked)
        {
            var e = entries.First(x => x.Id == kv.Key);
            results[kv.Key] = new EntryMatch
            {
                Entry = e,
                Outcome = MatchOutcome.Matched,
                Window = kv.Value,
                Candidates = cands[kv.Key],
                Note = "reserved"
            };
        }

        foreach (var eid in freeEntryIds)
        {
            var entry = entries.First(x => x.Id == eid);
            match.TryGetValue(eid, out var currentWinId);
            if (currentWinId is null)
            {
                var hasAny = cands[eid].Count > 0;
                results[eid] = new EntryMatch
                {
                    Entry = entry,
                    Outcome = hasAny ? MatchOutcome.Ambiguous : MatchOutcome.NoMatch,
                    Candidates = cands[eid],
                    Note = hasAny ? $"candidate_count={cands[eid].Count}" : null
                };
                continue;
            }

            // Forced-edge test: remove this edge and recompute max matching on the same free set.
            // If cardinality is preserved, the entry has an equivalent alternative — Ambiguous.
            // If cardinality drops, this edge is in every max matching — Matched.
            var withoutEdge = new Dictionary<string, List<WindowInfo>>(StringComparer.Ordinal);
            foreach (var kv in cands) withoutEdge[kv.Key] = kv.Value.ToList();
            withoutEdge[eid].RemoveAll(c => c.Identity.Equals(currentWinId));
            var alt = MaxMatching(freeEntryIds, withoutEdge);
            int altSize = alt.Count(kv => kv.Value is not null);
            if (altSize == matchSize)
            {
                results[eid] = new EntryMatch
                {
                    Entry = entry,
                    Outcome = MatchOutcome.Ambiguous,
                    Candidates = cands[eid],
                    Note = $"candidate_count={cands[eid].Count}"
                };
            }
            else
            {
                var w = winById[currentWinId];
                results[eid] = new EntryMatch
                {
                    Entry = entry,
                    Outcome = MatchOutcome.Matched,
                    Window = w,
                    Candidates = cands[eid]
                };
            }
        }

        return entries.Select(e => results[e.Id]).ToList();
    }

    /// <summary>Simple augmenting-path max bipartite matching. O(V*E), sufficient for profile sizes.</summary>
    private static Dictionary<string, WindowIdentity?> MaxMatching(List<string> entries, Dictionary<string, List<WindowInfo>> cands)
    {
        var matchEntry = new Dictionary<string, WindowIdentity?>(StringComparer.Ordinal);
        var matchWin = new Dictionary<WindowIdentity, string>();
        foreach (var e in entries) matchEntry[e] = null;
        foreach (var e in entries)
        {
            var visited = new HashSet<WindowIdentity>();
            TryAugment(e, cands, matchEntry, matchWin, visited);
        }
        return matchEntry;
    }

    private static bool TryAugment(string entry, Dictionary<string, List<WindowInfo>> cands,
        Dictionary<string, WindowIdentity?> matchEntry, Dictionary<WindowIdentity, string> matchWin,
        HashSet<WindowIdentity> visited)
    {
        if (!cands.ContainsKey(entry)) return false;
        foreach (var c in cands[entry])
        {
            if (!visited.Add(c.Identity)) continue;
            if (!matchWin.TryGetValue(c.Identity, out var holder) || TryAugment(holder, cands, matchEntry, matchWin, visited))
            {
                matchEntry[entry] = c.Identity;
                matchWin[c.Identity] = entry;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Strict match predicate: exe path (case-insensitive) + exact class name + titleContains substring (when specified).
    /// A missing titleContains means "any title accepted for this entry".
    /// </summary>
    public static bool StrictMatches(ProfileEntry entry, WindowInfo w)
    {
        if (!string.Equals(w.ExeKey, entry.ExeKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(w.ClassName, entry.ClassName, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(entry.TitleContains) &&
            !w.Title.Contains(entry.TitleContains, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Looser predicate (exe+class only). Used by monitoring to decide whether a window
    /// COULD plausibly match an entry if its title finishes initializing.</summary>
    public static bool CouldPotentiallyMatch(ProfileEntry entry, WindowInfo w)
    {
        if (!string.Equals(w.ExeKey, entry.ExeKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(w.ClassName, entry.ClassName, StringComparison.Ordinal)) return false;
        return true;
    }

    private static List<WindowInfo> StrictCandidatesFor(ProfileEntry entry, IEnumerable<WindowInfo> windows)
    {
        var list = new List<WindowInfo>();
        foreach (var w in windows)
        {
            if (StrictMatches(entry, w)) list.Add(w);
        }
        return list;
    }

    public static WindowRect ClampToWorkArea(WindowRect target, IReadOnlyList<MonitorInfo> monitors, out bool adjusted)
    {
        adjusted = false;
        if (monitors.Count == 0) return target;

        MonitorInfo best = monitors[0];
        int bestOverlap = -1;
        foreach (var m in monitors)
        {
            int overlap = IntersectArea(target, m.WorkArea);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = m; }
        }
        if (bestOverlap <= 0)
        {
            MonitorInfo nearest = monitors[0];
            long bestDist = long.MaxValue;
            foreach (var m in monitors)
            {
                long dx = (target.X + target.Width / 2) - (m.WorkArea.X + m.WorkArea.Width / 2);
                long dy = (target.Y + target.Height / 2) - (m.WorkArea.Y + m.WorkArea.Height / 2);
                long d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; nearest = m; }
            }
            best = nearest;
        }

        var wa = best.WorkArea;
        int w = Math.Min(target.Width, wa.Width);
        int h = Math.Min(target.Height, wa.Height);
        int x = Math.Max(wa.X, Math.Min(target.X, wa.X + wa.Width - w));
        int y = Math.Max(wa.Y, Math.Min(target.Y, wa.Y + wa.Height - h));
        var clamped = new WindowRect(x, y, w, h);
        adjusted = !(x == target.X && y == target.Y && w == target.Width && h == target.Height);
        return clamped;
    }

    private static int IntersectArea(WindowRect a, WindowRect b)
    {
        int l = Math.Max(a.X, b.X);
        int t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.Right, b.Right);
        int bb = Math.Min(a.Bottom, b.Bottom);
        if (r <= l || bb <= t) return 0;
        return (r - l) * (bb - t);
    }

    public static string SuggestTitleContainsFor(string title, IEnumerable<string> siblingTitles)
    {
        var tokens = title.Split(new[] { ' ', '-', '—', '–', '|', ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.Length < 3) continue;
            if (!siblingTitles.Any(s => s.Contains(token, StringComparison.OrdinalIgnoreCase))) return token;
        }
        return title.Length <= 24 ? title : title.Substring(0, 24);
    }
}
