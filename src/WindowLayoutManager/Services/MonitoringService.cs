using WindowLayoutManager.Model;
using WindowLayoutManager.Native;

namespace WindowLayoutManager.Services;

public enum PlaceResult
{
    Moved,
    Skipped_NoMatch,
    Skipped_Ambiguous,
    Skipped_SlotOccupied,
    Skipped_NativeFailed,
    Skipped_IdentityStale,
    Skipped_PendingRetry,
    Skipped_Rejected,
}

public sealed record PlaceReport(string EntryId, string EntryRole, PlaceResult Result, WindowIdentity? WindowId, string? Note, WindowRect TargetRect, bool Adjusted);

public sealed class MonitoringService
{
    private readonly IWindowProvider _provider;
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly object _lock = new();

    private Profile? _activeProfile;
    private bool _monitoringEnabled;

    /// <summary>
    /// entryId → identity currently occupying the slot. Preserved across visibility/cloaked changes
    /// until the identity is confirmed dead via IWindowProvider.IsAlive.
    /// </summary>
    private readonly Dictionary<string, WindowIdentity> _entrySlot = new();

    /// <summary>
    /// entryId → predicate snapshot (exe|class|titleContains) captured when the binding was created.
    /// An explicit profile edit that changes the matching predicate invalidates the binding on the
    /// next refresh. This is distinct from a live window's title naturally changing: that would be
    /// detected only by live metadata, which we deliberately do NOT re-check for an unchanged
    /// predicate (so a user-driven window title tweak never releases the slot).
    /// </summary>
    private readonly Dictionary<string, string> _entrySlotPredicate = new();

    private static string PredicateKey(ProfileEntry e) =>
        $"{e.ExeKey}|{e.ClassName}|{e.TitleContains ?? string.Empty}";

    /// <summary>
    /// Instance tokens we have already processed. New windows (identity not here) are candidates
    /// for one-shot automatic placement.
    /// </summary>
    private readonly HashSet<WindowIdentity> _seen = new();

    /// <summary>
    /// Stabilization retries for new windows whose title/process data isn't ready for a confident match.
    /// Bounded by StabilizationMaxTicks to avoid retrying forever.
    /// </summary>
    private readonly Dictionary<WindowIdentity, int> _pending = new();

    public const int StabilizationMaxTicks = 5;

    public event Action<string>? LogAppended;
    public event Action<IReadOnlyList<PlaceReport>>? AutoReportsEmitted;

    public MonitoringService(IWindowProvider provider, ProfileStore profiles, SettingsStore settings)
    {
        _provider = provider;
        _profiles = profiles;
        _settings = settings;
    }

    public bool IsMonitoring { get { lock (_lock) return _monitoringEnabled; } }
    public Profile? ActiveProfile { get { lock (_lock) return _activeProfile; } }
    public IReadOnlyDictionary<string, WindowIdentity> SlotSnapshot()
    {
        lock (_lock) return _entrySlot.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public void InitializeFromSettings()
    {
        lock (_lock)
        {
            var s = _settings.Load();
            _monitoringEnabled = s.MonitoringEnabled;
            if (!string.IsNullOrEmpty(s.ActiveProfileId))
            {
                try { _activeProfile = _profiles.Load(s.ActiveProfileId); }
                catch (Exception ex) { Log($"프로파일 로드 실패: {ex.Message}"); _activeProfile = null; }
            }
            // Prime seen & reserve slots so startup never rearranges, regardless of monitoring state.
            PrimeFromCurrent();
        }
    }

    /// <summary>
    /// Primes identity "seen" set + reserves profile slots WITHOUT moving any window:
    ///  (a) Preserves existing _entrySlot bindings ONLY when (1) the entry id still exists in the
    ///      current active profile AND (2) the window identity is still IsAlive. This prunes stale
    ///      bindings on profile switch and on deleted/edited entries.
    ///  (b) Wide-enumerates ALL top-level windows (visible + hidden/cloaked) and adds their
    ///      identities to _seen so hidden-at-start windows later shown are not treated as "new".
    ///  (c) For entries without a preserved slot, runs the strict matcher against the UNION of
    ///      visible + hidden-with-metadata and reserves unambiguous matches. Hidden windows CAN
    ///      receive a reservation; they are never moved.
    /// </summary>
    private void PrimeFromCurrent()
    {
        // (a) Save existing bindings whose entry id is still in the active profile AND identity alive
        //     AND the saved predicate still matches the CURRENT entry's predicate. An explicit edit
        //     to exe/class/titleContains therefore invalidates the stale binding. Preservation does
        //     NOT re-check against live window metadata — an unchanged entry whose window naturally
        //     retitled stays reserved.
        var preserved = new Dictionary<string, WindowIdentity>(StringComparer.Ordinal);
        var preservedPred = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_activeProfile is not null)
        {
            foreach (var e in _activeProfile.Entries)
            {
                if (!_entrySlot.TryGetValue(e.Id, out var id)) continue;
                if (!_entrySlotPredicate.TryGetValue(e.Id, out var savedKey)) continue;
                if (savedKey != PredicateKey(e)) continue; // explicit definition changed → invalidate
                try
                {
                    if (_provider.IsAlive(id))
                    {
                        preserved[e.Id] = id;
                        preservedPred[e.Id] = savedKey;
                    }
                }
                catch { }
            }
        }

        _seen.Clear();
        _pending.Clear();
        _entrySlot.Clear();
        _entrySlotPredicate.Clear();

        // (b) Wide prime via identity-only probe.
        try
        {
            foreach (var id in _provider.EnumerateAnyTopLevelIdentities()) _seen.Add(id);
        }
        catch (Exception ex) { Log($"wide prime 실패: {ex.Message}"); }

        // Restore preserved bindings.
        foreach (var kv in preserved)
        {
            _entrySlot[kv.Key] = kv.Value;
            _entrySlotPredicate[kv.Key] = preservedPred[kv.Key];
        }

        if (_activeProfile is null) return;

        var visibleSnap = _provider.Enumerate();
        foreach (var w in visibleSnap.Windows) _seen.Add(w.Identity);

        // (c) Union visible + hidden-with-metadata, then run strict matcher so hidden identifiable
        //     windows get a slot reservation at cold startup. Reservations never trigger a move.
        List<WindowInfo> metaUnion;
        try
        {
            var hidden = _provider.EnumerateAnyTopLevelWithMetadata();
            var visibleIds = visibleSnap.Windows.Select(w => w.Identity).ToHashSet();
            metaUnion = new List<WindowInfo>(visibleSnap.Windows);
            foreach (var h in hidden)
            {
                if (visibleIds.Contains(h.Identity)) continue;
                metaUnion.Add(h);
                _seen.Add(h.Identity);
            }
        }
        catch (Exception ex)
        {
            Log($"hidden metadata prime 실패: {ex.Message}");
            metaUnion = visibleSnap.Windows.ToList();
        }

        // Build a sub-profile of entries still needing a slot and match.
        var remainingEntries = _activeProfile.Entries.Where(e => !_entrySlot.ContainsKey(e.Id)).ToList();
        if (remainingEntries.Count == 0) return;
        // Exclude windows already reserved by preserved bindings from the candidate pool.
        var reservedWinIds = _entrySlot.Values.ToHashSet();
        var candidatePool = metaUnion.Where(w => !reservedWinIds.Contains(w.Identity)).ToList();
        var sub = new Profile { Id = _activeProfile.Id, Name = _activeProfile.Name, Version = _activeProfile.Version, Entries = remainingEntries };
        var matches = LayoutEngine.MatchProfile(sub, candidatePool);
        foreach (var m in matches)
        {
            if (m.Outcome == MatchOutcome.Matched && m.Window is not null)
            {
                _entrySlot[m.Entry.Id] = m.Window.Identity;
                _entrySlotPredicate[m.Entry.Id] = PredicateKey(m.Entry);
            }
        }
    }

    public void SetActiveProfile(Profile? profile)
    {
        lock (_lock)
        {
            _activeProfile = profile;
            PrimeFromCurrent();
            Persist();
        }
    }

    /// <summary>
    /// Called after the user edits/updates the active profile on disk. Refreshes the in-memory
    /// active model by ID and re-primes occupancy WITHOUT moving existing windows.
    /// </summary>
    public void RefreshActiveFromDisk()
    {
        lock (_lock)
        {
            if (_activeProfile is null) return;
            try
            {
                var reloaded = _profiles.Load(_activeProfile.Id);
                if (reloaded != null) _activeProfile = reloaded;
            }
            catch (Exception ex)
            {
                Log($"활성 프로파일 새로고침 실패: {ex.Message}");
            }
            PrimeFromCurrent();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _monitoringEnabled = false;
            Persist();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _monitoringEnabled = true;
            PrimeFromCurrent();
            Persist();
        }
    }

    public string? LastPersistError { get; private set; }

    private void Persist()
    {
        try
        {
            _settings.Save(new AppSettings
            {
                ActiveProfileId = _activeProfile?.Id,
                MonitoringEnabled = _monitoringEnabled
            });
            LastPersistError = null;
        }
        catch (Exception ex)
        {
            // Surface via LogAppended + remember the error so callers can show truthful status.
            // Do NOT swallow silently; do NOT corrupt the in-memory state. Session-level monitoring
            // state is kept as the user requested; only disk persistence failed.
            LastPersistError = ex.Message;
            Log($"설정 저장 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// Explicit apply: moves open matching windows AND enables + persists monitoring so that
    /// future new instances get placed. Returns per-entry outcomes.
    /// </summary>
    public IReadOnlyList<PlaceReport> ApplyActiveProfile()
    {
        lock (_lock)
        {
            if (_activeProfile is null) return Array.Empty<PlaceReport>();
            return ApplyProfileInternal(_activeProfile, enableMonitoring: true);
        }
    }

    /// <summary>
    /// Apply the given profile. By default (when the given profile IS the active profile) this
    /// also enables monitoring. When applying ad-hoc (e.g. CLI), monitoring is NOT auto-enabled
    /// unless the given profile is the active one.
    /// </summary>
    public IReadOnlyList<PlaceReport> ApplyProfile(Profile profile)
    {
        lock (_lock)
        {
            bool isActive = _activeProfile != null && _activeProfile.Id == profile.Id;
            return ApplyProfileInternal(profile, enableMonitoring: isActive);
        }
    }

    private List<PlaceReport> ApplyProfileInternal(Profile profile, bool enableMonitoring)
    {
        var visibleSnap = _provider.Enumerate();

        // Union visible + hidden-with-metadata so the matcher can honor reservations against
        // hidden-but-alive windows (prevents a later competing B from stealing the slot after Apply).
        List<WindowInfo> allMeta;
        try
        {
            var hidden = _provider.EnumerateAnyTopLevelWithMetadata();
            var visIds = visibleSnap.Windows.Select(w => w.Identity).ToHashSet();
            allMeta = new List<WindowInfo>(visibleSnap.Windows);
            foreach (var h in hidden) if (!visIds.Contains(h.Identity)) allMeta.Add(h);
        }
        catch (Exception ex) { Log($"apply hidden metadata enum 실패: {ex.Message}"); allMeta = visibleSnap.Windows.ToList(); }

        bool isActive = _activeProfile != null && _activeProfile.Id == profile.Id;
        var visibleIds = visibleSnap.Windows.Select(w => w.Identity).ToHashSet();

        IReadOnlyDictionary<string, WindowIdentity> reservations;
        if (isActive)
        {
            // Prune stale bindings: entries whose id no longer exists in the current profile; whose
            // saved predicate no longer matches the current entry definition (explicit edit); or
            // whose identity is dead. Preserve the rest (including those pointing at hidden windows).
            var alive = new Dictionary<string, WindowIdentity>(StringComparer.Ordinal);
            var alivePred = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in profile.Entries)
            {
                if (!_entrySlot.TryGetValue(e.Id, out var id)) continue;
                if (!_entrySlotPredicate.TryGetValue(e.Id, out var savedKey)) continue;
                if (savedKey != PredicateKey(e)) continue;
                try { if (_provider.IsAlive(id)) { alive[e.Id] = id; alivePred[e.Id] = savedKey; } } catch { }
            }
            _entrySlot.Clear();
            _entrySlotPredicate.Clear();
            foreach (var kv in alive)
            {
                _entrySlot[kv.Key] = kv.Value;
                _entrySlotPredicate[kv.Key] = alivePred[kv.Key];
            }
            reservations = _entrySlot;
        }
        else
        {
            reservations = new Dictionary<string, WindowIdentity>();
        }

        var matches = LayoutEngine.MatchProfile(profile, allMeta, reservations);
        var reports = new List<PlaceReport>();

        foreach (var m in matches)
        {
            if (m.Outcome == MatchOutcome.NoMatch)
            {
                reports.Add(new PlaceReport(m.Entry.Id, m.Entry.Role, PlaceResult.Skipped_NoMatch, null, "현재 열린 창 없음", m.Entry.Rect.ToWindowRect(), false));
                continue;
            }
            if (m.Outcome == MatchOutcome.Ambiguous)
            {
                reports.Add(new PlaceReport(m.Entry.Id, m.Entry.Role, PlaceResult.Skipped_Ambiguous, null, m.Note, m.Entry.Rect.ToWindowRect(), false));
                continue;
            }
            if (m.Window is null) continue;

            // Hidden reserved window: keep the slot, DO NOT move (hidden windows cannot be moved
            // visually without unhiding; unhiding would surprise the user).
            if (!visibleIds.Contains(m.Window.Identity))
            {
                if (isActive)
                {
                    _entrySlot[m.Entry.Id] = m.Window.Identity;
                    _entrySlotPredicate[m.Entry.Id] = PredicateKey(m.Entry);
                    _seen.Add(m.Window.Identity);
                }
                reports.Add(new PlaceReport(m.Entry.Id, m.Entry.Role, PlaceResult.Skipped_SlotOccupied,
                    m.Window.Identity, "hidden_reserved", m.Entry.Rect.ToWindowRect(), false));
                continue;
            }

            var target = LayoutEngine.ClampToWorkArea(m.Entry.Rect.ToWindowRect(), visibleSnap.Monitors, out var adjusted);
            var mv = _provider.Move(m.Window.Identity, target, m.Entry.Maximized, m.Entry.ExeKey, m.Entry.ClassName);
            var result = mv.Outcome switch
            {
                MoveOutcome.Success => PlaceResult.Moved,
                MoveOutcome.IdentityChanged => PlaceResult.Skipped_IdentityStale,
                MoveOutcome.WindowGone => PlaceResult.Skipped_IdentityStale,
                MoveOutcome.Rejected => PlaceResult.Skipped_Rejected,
                MoveOutcome.Unresponsive => PlaceResult.Skipped_NativeFailed,
                _ => PlaceResult.Skipped_NativeFailed,
            };
            if (result == PlaceResult.Moved && isActive)
            {
                _entrySlot[m.Entry.Id] = m.Window.Identity;
                _entrySlotPredicate[m.Entry.Id] = PredicateKey(m.Entry);
            }
            if (isActive)
            {
                _seen.Add(m.Window.Identity);
                _pending.Remove(m.Window.Identity);
            }
            reports.Add(new PlaceReport(m.Entry.Id, m.Entry.Role, result, m.Window.Identity, mv.Error, target, adjusted));
        }

        if (isActive)
        {
            // Mark ALL other eligible windows as seen so monitoring won't later treat them as "new".
            foreach (var w in visibleSnap.Windows)
            {
                _seen.Add(w.Identity);
            }
            if (enableMonitoring)
            {
                _monitoringEnabled = true;
            }
            Persist();
        }
        return reports;
    }

    /// <summary>
    /// Timer tick. Only moves BRAND-NEW windows (identity not in seen set) that strictly match
    /// an open slot via the shared bipartite matcher. Never moves existing windows.
    /// </summary>
    public IReadOnlyList<PlaceReport> Tick()
    {
        lock (_lock)
        {
            if (_activeProfile is null || !_monitoringEnabled) return Array.Empty<PlaceReport>();

            var snap = _provider.Enumerate();
            var currentIds = snap.Windows.Select(w => w.Identity).ToHashSet();

            // LIVENESS-based pruning: a window that becomes hidden/cloaked is NOT gone. We only
            // release a slot or drop from _seen when the native identity is confirmed dead.
            var releaseSlots = new List<string>();
            foreach (var kv in _entrySlot)
            {
                if (!currentIds.Contains(kv.Value) && !_provider.IsAlive(kv.Value))
                {
                    releaseSlots.Add(kv.Key);
                }
            }
            foreach (var k in releaseSlots) { _entrySlot.Remove(k); _entrySlotPredicate.Remove(k); }

            var staleSeen = new List<WindowIdentity>();
            foreach (var id in _seen)
            {
                if (!currentIds.Contains(id) && !_provider.IsAlive(id)) staleSeen.Add(id);
            }
            foreach (var id in staleSeen) _seen.Remove(id);

            var stalePending = new List<WindowIdentity>();
            foreach (var id in _pending.Keys)
            {
                if (!currentIds.Contains(id) && !_provider.IsAlive(id)) stalePending.Add(id);
            }
            foreach (var id in stalePending) _pending.Remove(id);

            // Collect new windows (identity not yet seen).
            var newWindows = snap.Windows.Where(w => !_seen.Contains(w.Identity)).ToList();
            if (newWindows.Count == 0) return Array.Empty<PlaceReport>();

            // Run the SHARED matcher against the profile with current slot reservations.
            // Only entries whose slot is currently empty are eligible for new placement.
            var profileForNewOnly = new Profile
            {
                Id = _activeProfile.Id,
                Name = _activeProfile.Name,
                Version = _activeProfile.Version,
                Entries = _activeProfile.Entries.Where(e => !_entrySlot.ContainsKey(e.Id)).ToList(),
            };
            // Exclude windows that already fill another slot — they are reserved and not candidates
            // for new placement. The remaining pool still contains all unassigned windows so the matcher
            // honestly reports ambiguity between competing new windows.
            var reservedWinIds = _entrySlot.Values.ToHashSet();
            var candidatePool = snap.Windows.Where(w => !reservedWinIds.Contains(w.Identity)).ToList();
            var matches = LayoutEngine.MatchProfile(profileForNewOnly, candidatePool);

            // Partition: NEW window matches get moved once; other outcomes influence stabilization.
            var reports = new List<PlaceReport>();
            var movedNewIds = new HashSet<WindowIdentity>();
            foreach (var m in matches)
            {
                if (m.Outcome != MatchOutcome.Matched || m.Window is null) continue;
                if (_seen.Contains(m.Window.Identity)) continue; // existing — do not touch
                if (!newWindows.Any(nw => nw.Identity.Equals(m.Window.Identity))) continue;

                // Place it.
                var target = LayoutEngine.ClampToWorkArea(m.Entry.Rect.ToWindowRect(), snap.Monitors, out var adjusted);
                var mv = _provider.Move(m.Window.Identity, target, m.Entry.Maximized, m.Entry.ExeKey, m.Entry.ClassName);
                var result = mv.Outcome switch
                {
                    MoveOutcome.Success => PlaceResult.Moved,
                    MoveOutcome.IdentityChanged => PlaceResult.Skipped_IdentityStale,
                    MoveOutcome.WindowGone => PlaceResult.Skipped_IdentityStale,
                    MoveOutcome.Rejected => PlaceResult.Skipped_Rejected,
                    MoveOutcome.Unresponsive => PlaceResult.Skipped_NativeFailed,
                    _ => PlaceResult.Skipped_NativeFailed,
                };
                if (result == PlaceResult.Moved)
                {
                    _entrySlot[m.Entry.Id] = m.Window.Identity;
                    _entrySlotPredicate[m.Entry.Id] = PredicateKey(m.Entry);
                    Log($"자동 배치: {m.Entry.Role} ← {m.Window.ProcessName} '{m.Window.Title}'");
                }
                _seen.Add(m.Window.Identity);
                _pending.Remove(m.Window.Identity);
                movedNewIds.Add(m.Window.Identity);
                reports.Add(new PlaceReport(m.Entry.Id, m.Entry.Role, result, m.Window.Identity, mv.Error, target, adjusted));
            }

            // Unresolved new windows → stabilize (title may still be initializing) or give up after N ticks.
            foreach (var nw in newWindows)
            {
                if (movedNewIds.Contains(nw.Identity)) continue;

                // Use the LOOSER predicate (exe+class) for the "might ever match" decision so that
                // a brand-new window with a still-loading title is not permanently marked seen before
                // its title arrives. Strict title still gates actual placement.
                var mightEverMatch = _activeProfile.Entries.Any(e => LayoutEngine.CouldPotentiallyMatch(e, nw));
                if (!mightEverMatch)
                {
                    _seen.Add(nw.Identity);
                    _pending.Remove(nw.Identity);
                    continue;
                }

                // If every entry whose exe+class matches this window has its slot occupied,
                // the window has no destination slot (fixed-slot semantics) → skip once.
                var openSameAppExists = _activeProfile.Entries.Any(e => LayoutEngine.CouldPotentiallyMatch(e, nw) && !_entrySlot.ContainsKey(e.Id));
                if (!openSameAppExists)
                {
                    _seen.Add(nw.Identity);
                    _pending.Remove(nw.Identity);
                    reports.Add(new PlaceReport("", "", PlaceResult.Skipped_SlotOccupied, nw.Identity, "slot_occupied_by_existing", default, false));
                    continue;
                }

                var relevant = matches.FirstOrDefault(mm => mm.Candidates.Any(c => c.Identity.Equals(nw.Identity)));
                if (relevant != null && relevant.Outcome == MatchOutcome.Ambiguous)
                {
                    var tryResult = Stabilize(nw, "ambiguous_new_batch");
                    if (tryResult != null) reports.Add(tryResult);
                    continue;
                }

                // Open slot exists but no strict match yet (title likely still loading). Stabilize briefly.
                var r2 = Stabilize(nw, "awaiting_strict_match");
                if (r2 != null) reports.Add(r2);
            }

            if (reports.Count > 0) AutoReportsEmitted?.Invoke(reports);
            return reports;
        }
    }

    private PlaceReport? Stabilize(WindowInfo nw, string reason)
    {
        _pending.TryGetValue(nw.Identity, out int count);
        count++;
        if (count >= StabilizationMaxTicks)
        {
            _pending.Remove(nw.Identity);
            _seen.Add(nw.Identity);
            Log($"안정화 종료 — 건너뜀 ({reason}): {nw.ProcessName}");
            return new PlaceReport("", "", PlaceResult.Skipped_PendingRetry, nw.Identity, $"stabilization_exhausted:{reason}", default, false);
        }
        _pending[nw.Identity] = count;
        return null;
    }

    public (int seen, int slots, int pending) GetDiagnostics()
    {
        lock (_lock) return (_seen.Count, _entrySlot.Count, _pending.Count);
    }

    private void Log(string s) => LogAppended?.Invoke(s);
}
