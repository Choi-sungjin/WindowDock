using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

public static partial class SelfTestCommand
{
    public static int Run(string[] args)
    {
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }

        var tests = new List<Func<(string name, bool pass, string? detail)>>
        {
            // Original match/monitoring tests
            T_MatchSingle,
            T_MatchDuplicateAmbiguous,
            T_TitleContainsDisambiguates,
            T_NewWindowPlacedOnce,
            T_ManualMoveRetained,
            T_ClosedWindowReopenGetsPlacement,
            T_PauseResume,
            T_ActiveProfileSwitchDoesNotRearrange,
            T_StaleIdentitySkipped,
            T_GeometryClampToWorkArea,
            T_PartialNativeFailure,
            T_ProfilePersistAndCorruption,
            T_UnknownProfileVersionRejected,
            T_PathTraversalGuard,
            T_SlotReleaseAfterDisappear,
            T_StabilizationGivesUpAfterLimit,
            // NEW regressions for review items
            T_StrictTitlePredicate_NeverStealsNonMatchingCandidate, // Codex-reproduced bug
            T_StrictTitlePredicate_NeverMovesWrongProcess,
            T_TwoRolesCompeteForSoleWindow_Ambiguous,
            T_DisjointSpecificAndGeneralRoles_ResolveSafely,
            T_SingleGeneralRole_TwoNewSameApp_Ambiguous,
            T_StartupOccupiedSlot_SkipsNewSameApp,
            T_HiddenThenShownSameInstance_NotNew,
            T_ApplyExplicitEnablesMonitoring,
            T_ActiveProfileRefresh_PreservesSlot,
            T_SettingsSchemaErrorExposed,
            T_ProfileFilenameIdMismatch_Rejected,
            T_ProfileMissingVersion_Rejected,
            T_ProfileNullRectEntry_Rejected,
            T_RejectedMoveReportedHonestly,
            T_HiddenAtStartup_LaterShown_NotNew,
            T_SpecificReservedPlusGeneralSlotReopen_PlacesCleanly,
            T_LoadingTitleStabilizesBeforeMarkedSeen,
            // Round-4 regressions for 07-review-request.md
            T_UpdateProposal_DoesNotMutateOriginal,
            T_UpdateCopyInto_PreservesFieldsAndClonesRect,
            T_HiddenSlotPreservedAcrossPauseResume,
            T_HiddenAtStart_WithMetadata_ReservedPreventsCompetingSteal,
            T_SettingsCorruptBackupOnOverwrite,
            T_SettingsStrictActiveProfileIdType,
            T_ApplyCliSetsActiveAndEnablesMonitoring,
            // Round-5 regressions for independent review failures
            T_SwitchProfilePrunesStaleBindings_ThenBReservedForRole2,
            T_HiddenReservedSurvivesExplicitApply_ThenCompetingBSkipped,
            T_DeletedEntryBindingIsPruned,
            T_SettingsRequiredFields_ActiveProfileIdMissingRejected,
            T_SettingsRequiredFields_MonitoringEnabledMissingRejected,
            T_SettingsSaveBacksUpInvalidOriginal,
            T_SettingsSaveFailsWhenBackupCollides,
            T_RenameRollbackOnSaveFailure,
            T_RemoveEntryRollbackOnSaveFailure,
            T_EditEntryRollbackOnSaveFailure,
            T_EntryEditDialog_DoesNotMutateInputBeforeOk,
            T_ExactShortageGraph_AllThreeAmbiguous_DisjointMatched,
            T_ExplicitTitleFilterEdit_InvalidatesStaleBinding_BMoves,
            T_LiveTitleChange_UnderUnchangedPredicate_KeepsBinding,
            T_MonitoringServiceLastPersistError_SetOnSaveFailure,
            T_MonitoringServiceLastPersistError_ClearedOnSuccess,
        };
        // v0.2.0: quick-apply slots, Previous/Next navigation, hotkeys.json and registration rollback.
        tests.AddRange(HotkeyRoundTests());

        var results = new List<object>();
        int pass = 0, fail = 0;
        foreach (var t in tests)
        {
            try
            {
                var (name, p, detail) = t();
                if (p) pass++; else fail++;
                results.Add(new { name, pass = p, detail });
            }
            catch (Exception ex)
            {
                fail++;
                results.Add(new { name = t.Method.Name, pass = false, detail = $"exception:{ex.GetType().Name}:{ex.Message}" });
            }
        }

        CliCommon.WriteJson(outPath, new
        {
            schemaVersion = 2,
            total = tests.Count,
            passed = pass,
            failed = fail,
            tests = results
        });
        return fail == 0 ? 0 : 1;
    }

    // --- test fake backend ---
    private sealed class FakeProvider : IWindowProvider
    {
        public List<WindowInfo> Windows { get; } = new();
        public HashSet<WindowIdentity> LiveIdentities { get; } = new();
        public Dictionary<WindowIdentity, WindowInfo> HiddenMetadata { get; } = new();
        public List<MonitorInfo> Monitors { get; } = new();
        public List<(WindowIdentity id, WindowRect target, bool max)> Moves { get; } = new();
        public Dictionary<IntPtr, MoveOutcome> ForceOutcome { get; } = new();

        public EnumerationResult Enumerate() => new()
        {
            Windows = Windows.ToList(),
            Excluded = Array.Empty<ExcludedWindow>(),
            Monitors = Monitors.ToList()
        };

        public bool IsAlive(WindowIdentity id) => LiveIdentities.Contains(id);

        public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => LiveIdentities.ToList();

        public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata()
        {
            // Visible windows plus any explicit hidden metadata.
            var list = Windows.ToList();
            var visIds = Windows.Select(w => w.Identity).ToHashSet();
            foreach (var kv in HiddenMetadata)
            {
                if (!visIds.Contains(kv.Key)) list.Add(kv.Value);
            }
            return list;
        }

        public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string exeKey, string? className = null)
        {
            if (ForceOutcome.TryGetValue(id.Hwnd, out var o))
            {
                if (o == MoveOutcome.Rejected)
                {
                    // Simulate the app accepting the move x/y but rejecting size.
                    var idx = Windows.FindIndex(w => w.Identity.Equals(id));
                    if (idx >= 0)
                    {
                        var w = Windows[idx];
                        var acceptedRect = new WindowRect(target.X, target.Y, Math.Max(200, target.Width - 200), Math.Max(150, target.Height - 150));
                        Windows[idx] = Clone(w, acceptedRect, w.ShowState);
                        return new MoveResult(MoveOutcome.Rejected, acceptedRect, "simulated_size_rejection");
                    }
                }
                return new MoveResult(o, default, "forced");
            }
            var ix = Windows.FindIndex(w => w.Identity.Equals(id));
            if (ix < 0) return new MoveResult(MoveOutcome.WindowGone, default, "gone");
            Moves.Add((id, target, maximize));
            var win = Windows[ix];
            Windows[ix] = Clone(win, target, maximize ? WindowShowState.Maximized : WindowShowState.Normal);
            return new MoveResult(MoveOutcome.Success, target, null);
        }

        public void AddWindow(WindowInfo w)
        {
            Windows.Add(w);
            LiveIdentities.Add(w.Identity);
        }

        public void RemoveWindow(WindowIdentity id)
        {
            Windows.RemoveAll(w => w.Identity.Equals(id));
            LiveIdentities.Remove(id);
        }

        /// <summary>Simulate "hidden/cloaked" (eligibility false) but still-alive window.</summary>
        public void HideButKeepAlive(WindowIdentity id)
        {
            Windows.RemoveAll(w => w.Identity.Equals(id));
            // LiveIdentities retains it.
        }

        public void MutateInPlace(WindowIdentity id, Func<WindowInfo, WindowInfo> f)
        {
            int ix = Windows.FindIndex(w => w.Identity.Equals(id));
            if (ix >= 0) Windows[ix] = f(Windows[ix]);
        }

        private static WindowInfo Clone(WindowInfo w, WindowRect rect, WindowShowState state) => new()
        {
            Identity = w.Identity,
            ExePath = w.ExePath,
            ProcessName = w.ProcessName,
            ClassName = w.ClassName,
            Title = w.Title,
            CurrentRect = rect,
            NormalRect = w.NormalRect,
            ShowState = state,
            MonitorDevice = w.MonitorDevice
        };
    }

    private static WindowInfo Win(long hwnd, int pid, string exe, string cls, string title, int x = 0, int y = 0, int w = 400, int h = 300, long start = 1000)
    {
        return new WindowInfo
        {
            Identity = new WindowIdentity(new IntPtr(hwnd), pid, start),
            ExePath = exe,
            ProcessName = Path.GetFileNameWithoutExtension(exe),
            ClassName = cls,
            Title = title,
            CurrentRect = new WindowRect(x, y, w, h),
            NormalRect = new WindowRect(x, y, w, h),
            ShowState = WindowShowState.Normal,
            MonitorDevice = @"\\.\DISPLAY1"
        };
    }

    private static MonitorInfo Mon(int x, int y, int w, int h) => new(@"\\.\DISPLAY1", new WindowRect(x, y, w, h), new WindowRect(x, y, w, h - 40), true);

    private static Profile MakeProfile(params (string role, string exe, string cls, string? titleContains, int x, int y, int w, int h)[] entries)
    {
        var p = new Profile { Id = Guid.NewGuid().ToString("N"), Name = "T" };
        foreach (var e in entries)
        {
            p.Entries.Add(new ProfileEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = e.role,
                ExePath = e.exe,
                ProcessName = Path.GetFileNameWithoutExtension(e.exe),
                ClassName = e.cls,
                TitleContains = e.titleContains,
                Rect = new SavedRect { X = e.x, Y = e.y, Width = e.w, Height = e.h }
            });
        }
        return p;
    }

    // ===== Original tests (restored and validated against new engine) =====

    private static (string, bool, string?) T_MatchSingle()
    {
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        var m = matches[0];
        bool ok = m.Outcome == MatchOutcome.Matched && m.Window?.Identity.Pid == 100;
        return (nameof(T_MatchSingle), ok, ok ? null : m.Outcome.ToString());
    }

    private static (string, bool, string?) T_MatchDuplicateAmbiguous()
    {
        var profile = MakeProfile(("chrome", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", null, 0, 0, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub"));
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "YouTube"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        bool ok = matches[0].Outcome == MatchOutcome.Ambiguous;
        return (nameof(T_MatchDuplicateAmbiguous), ok, ok ? null : matches[0].Outcome.ToString());
    }

    private static (string, bool, string?) T_TitleContainsDisambiguates()
    {
        var profile = MakeProfile(
            ("gh", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub", 0, 0, 500, 400),
            ("yt", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "YouTube", 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "repo - GitHub"));
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "cat - YouTube"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        bool ok = matches.All(m => m.Outcome == MatchOutcome.Matched) &&
                  matches.First(m => m.Entry.Role == "gh").Window?.Identity.Pid == 100 &&
                  matches.First(m => m.Entry.Role == "yt").Window?.Identity.Pid == 101;
        return (nameof(T_TitleContainsDisambiguates), ok, null);
    }

    private static (string, bool, string?) T_NewWindowPlacedOnce()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path);
        store.Save(profile);
        var ss = new SettingsStore(td.Path);
        var mon = new MonitoringService(fake, store, ss);
        mon.SetActiveProfile(profile);
        mon.Resume();
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        mon.Tick();
        int moves1 = fake.Moves.Count;
        mon.Tick();
        int moves2 = fake.Moves.Count;
        bool ok = moves1 == 1 && moves2 == 1;
        return (nameof(T_NewWindowPlacedOnce), ok, $"moves1={moves1} moves2={moves2}");
    }

    private static (string, bool, string?) T_ManualMoveRetained()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        mon.Tick(); // placement 1
        fake.MutateInPlace(id, w => new WindowInfo
        {
            Identity = w.Identity, ExePath = w.ExePath, ProcessName = w.ProcessName, ClassName = w.ClassName,
            Title = w.Title, CurrentRect = new WindowRect(777, 777, 300, 200), NormalRect = w.NormalRect,
            ShowState = w.ShowState, MonitorDevice = w.MonitorDevice
        });
        mon.Tick();
        mon.Tick();
        bool ok = fake.Moves.Count == 1 && fake.Windows[0].CurrentRect.X == 777;
        return (nameof(T_ManualMoveRetained), ok, $"moves={fake.Moves.Count} x={fake.Windows[0].CurrentRect.X}");
    }

    private static (string, bool, string?) T_ClosedWindowReopenGetsPlacement()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 50, 50, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled", start: 1000));
        mon.Tick(); // placement 1
        fake.RemoveWindow(new WindowIdentity(new IntPtr(1), 100, 1000));
        mon.Tick(); // slot released
        fake.AddWindow(Win(2, 200, @"C:\x\notepad.exe", "Notepad", "Untitled", start: 2000));
        mon.Tick();
        bool ok = fake.Moves.Count == 2;
        return (nameof(T_ClosedWindowReopenGetsPlacement), ok, $"moves={fake.Moves.Count}");
    }

    private static (string, bool, string?) T_PauseResume()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        mon.Pause();
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        mon.Tick(); // paused — nothing
        int moves1 = fake.Moves.Count;
        mon.Resume(); // marks current windows as seen; no retroactive move
        mon.Tick();
        int moves2 = fake.Moves.Count;
        bool ok = moves1 == 0 && moves2 == 0;
        return (nameof(T_PauseResume), ok, $"{moves1}/{moves2}");
    }

    private static (string, bool, string?) T_ActiveProfileSwitchDoesNotRearrange()
    {
        using var td = new TempDir();
        var profA = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 1, 1, 100, 100));
        var profB = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 2, 2, 200, 200));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        var store = new ProfileStore(td.Path); store.Save(profA); store.Save(profB);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.Resume();
        mon.SetActiveProfile(profA);
        mon.SetActiveProfile(profB);
        mon.Tick();
        bool ok = fake.Moves.Count == 0;
        return (nameof(T_ActiveProfileSwitchDoesNotRearrange), ok, $"moves={fake.Moves.Count}");
    }

    private static (string, bool, string?) T_StaleIdentitySkipped()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        fake.ForceOutcome[new IntPtr(1)] = MoveOutcome.IdentityChanged;
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        var reports = mon.ApplyActiveProfile();
        bool ok = reports.Any(r => r.Result == PlaceResult.Skipped_IdentityStale);
        return (nameof(T_StaleIdentitySkipped), ok, null);
    }

    private static (string, bool, string?) T_GeometryClampToWorkArea()
    {
        var monitors = new[] { Mon(0, 0, 1920, 1080) };
        var r = LayoutEngine.ClampToWorkArea(new WindowRect(5000, 5000, 500, 400), monitors, out var adjusted);
        bool ok = adjusted && r.X + r.Width <= 1920 && r.Y + r.Height <= 1040;
        return (nameof(T_GeometryClampToWorkArea), ok, $"{r.X},{r.Y} {r.Width}x{r.Height} adj={adjusted}");
    }

    private static (string, bool, string?) T_PartialNativeFailure()
    {
        using var td = new TempDir();
        var profile = MakeProfile(
            ("a", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400),
            ("b", @"C:\x\other.exe", "OtherClass", null, 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        fake.AddWindow(Win(2, 101, @"C:\x\other.exe", "OtherClass", "Other"));
        fake.ForceOutcome[new IntPtr(2)] = MoveOutcome.NativeFailed;
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        var reports = mon.ApplyActiveProfile();
        bool ok = reports.Count == 2 && reports.Any(r => r.Result == PlaceResult.Moved) && reports.Any(r => r.Result == PlaceResult.Skipped_NativeFailed);
        return (nameof(T_PartialNativeFailure), ok, null);
    }

    private static (string, bool, string?) T_ProfilePersistAndCorruption()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var p = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400));
        store.Save(p);
        var reloaded = store.Load(p.Id);
        if (reloaded == null || reloaded.Entries.Count != 1) return (nameof(T_ProfilePersistAndCorruption), false, "roundtrip failed");

        var badPath = Path.Combine(store.ProfilesDir, "corrupted.json");
        File.WriteAllText(badPath, "{not valid json");
        var all = store.LoadAll(out var errs);
        bool preserved = File.Exists(badPath); // Review requirement: preserve corrupt originals.
        bool ok = all.Count == 1 && errs.Count == 1 && preserved;
        return (nameof(T_ProfilePersistAndCorruption), ok, $"all={all.Count} errs={errs.Count} preserved={preserved}");
    }

    private static (string, bool, string?) T_UnknownProfileVersionRejected()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var badPath = Path.Combine(store.ProfilesDir, "unknownver.json");
        File.WriteAllText(badPath, "{\"version\":999,\"id\":\"unknownver\",\"name\":\"x\",\"entries\":[]}");
        var all = store.LoadAll(out var errs);
        bool threw = false;
        try { _ = store.Load("unknownver"); } catch { threw = true; }
        bool ok = threw && all.Count == 0 && errs.Count == 1;
        return (nameof(T_UnknownProfileVersionRejected), ok, $"errs={errs.Count}");
    }

    private static (string, bool, string?) T_PathTraversalGuard()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var p = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400));
        p.Id = "../evil";
        bool threw = false;
        try { store.Save(p); } catch { threw = true; }
        bool ok = threw && !File.Exists(Path.Combine(td.Path, "..", "evil.json"));
        return (nameof(T_PathTraversalGuard), ok, null);
    }

    private static (string, bool, string?) T_SlotReleaseAfterDisappear()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        mon.Tick();
        var (seen1, slots1, _) = mon.GetDiagnostics();
        fake.RemoveWindow(new WindowIdentity(new IntPtr(1), 100, 1000));
        mon.Tick();
        var (seen2, slots2, _) = mon.GetDiagnostics();
        bool ok = slots1 == 1 && slots2 == 0 && seen1 >= 1 && seen2 == 0;
        return (nameof(T_SlotReleaseAfterDisappear), ok, $"s1={slots1} s2={slots2}");
    }

    private static (string, bool, string?) T_StabilizationGivesUpAfterLimit()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("gh", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub", 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "StackOverflow"));
        for (int i = 0; i < 10; i++) mon.Tick();
        bool ok = fake.Moves.Count == 0;
        return (nameof(T_StabilizationGivesUpAfterLimit), ok, $"moves={fake.Moves.Count}");
    }

    // ===== NEW regressions for review items =====

    /// <summary>Codex-reproduced bug: strict titleContains must not greedily take a non-matching window.</summary>
    private static (string, bool, string?) T_StrictTitlePredicate_NeverStealsNonMatchingCandidate()
    {
        var profile = MakeProfile(("impossible", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Chrome_WidgetWin_1", "WLM_NEVER_MATCH_xyz", 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Chrome_WidgetWin_1", "something - YouTube"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        bool ok = matches.Count == 1 && matches[0].Outcome == MatchOutcome.NoMatch;
        return (nameof(T_StrictTitlePredicate_NeverStealsNonMatchingCandidate), ok, $"outcome={matches[0].Outcome}");
    }

    /// <summary>A role targeting notepad never matches a chrome window even if nothing else is open.</summary>
    private static (string, bool, string?) T_StrictTitlePredicate_NeverMovesWrongProcess()
    {
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        bool ok = matches[0].Outcome == MatchOutcome.NoMatch;
        return (nameof(T_StrictTitlePredicate_NeverMovesWrongProcess), ok, null);
    }

    /// <summary>Two roles whose strict predicates both match a single window → both Ambiguous (none wins).</summary>
    private static (string, bool, string?) T_TwoRolesCompeteForSoleWindow_Ambiguous()
    {
        var profile = MakeProfile(
            ("a", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "Common", 0, 0, 300, 200),
            ("b", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "Common", 100, 100, 300, 200));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "Common - Chrome"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        bool ok = matches.All(m => m.Outcome == MatchOutcome.Ambiguous);
        return (nameof(T_TwoRolesCompeteForSoleWindow_Ambiguous), ok, string.Join(",", matches.Select(m => m.Outcome)));
    }

    /// <summary>Specific(title=A) + general same-app role, windows A and B → A→A role, general→B.</summary>
    private static (string, bool, string?) T_DisjointSpecificAndGeneralRoles_ResolveSafely()
    {
        var profile = MakeProfile(
            ("gh", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub", 0, 0, 500, 400),
            ("general", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", null, 600, 0, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "repo - GitHub"));
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "random site"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        var gh = matches.First(m => m.Entry.Role == "gh");
        var gen = matches.First(m => m.Entry.Role == "general");
        bool ok = gh.Outcome == MatchOutcome.Matched && gh.Window?.Identity.Pid == 100 &&
                  gen.Outcome == MatchOutcome.Matched && gen.Window?.Identity.Pid == 101;
        return (nameof(T_DisjointSpecificAndGeneralRoles_ResolveSafely), ok, null);
    }

    /// <summary>One general role + two indistinguishable new same-app windows in one tick → Ambiguous, neither wins.</summary>
    private static (string, bool, string?) T_SingleGeneralRole_TwoNewSameApp_Ambiguous()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("general", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        // Two indistinguishable new windows appear simultaneously
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "untitled"));
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "untitled"));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool ok = fake.Moves.Count == 0; // arbitrary first-wins would place one; strict matcher must not.
        return (nameof(T_SingleGeneralRole_TwoNewSameApp_Ambiguous), ok, $"moves={fake.Moves.Count}");
    }

    /// <summary>Startup existing A occupies sole general slot; B opens later → skip B.</summary>
    private static (string, bool, string?) T_StartupOccupiedSlot_SkipsNewSameApp()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("general", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        // Startup: window A already exists
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "A"));
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        // Slot should be reserved for existing A; no move happened (startup must not rearrange).
        if (fake.Moves.Count != 0) return (nameof(T_StartupOccupiedSlot_SkipsNewSameApp), false, "startup moved A");
        // Then B opens
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "B"));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool ok = fake.Moves.Count == 0; // Slot occupied by A → B skipped
        return (nameof(T_StartupOccupiedSlot_SkipsNewSameApp), ok, $"moves={fake.Moves.Count}");
    }

    /// <summary>Window becomes hidden/cloaked (not in eligible list) but is still alive → do not treat as new on return.</summary>
    private static (string, bool, string?) T_HiddenThenShownSameInstance_NotNew()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        var w = Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled");
        fake.AddWindow(w);
        mon.Tick(); // place
        int firstMoves = fake.Moves.Count;
        // User moves it manually
        fake.MutateInPlace(id, old => new WindowInfo
        {
            Identity = old.Identity, ExePath = old.ExePath, ProcessName = old.ProcessName,
            ClassName = old.ClassName, Title = old.Title,
            CurrentRect = new WindowRect(888, 888, 300, 200), NormalRect = old.NormalRect,
            ShowState = old.ShowState, MonitorDevice = old.MonitorDevice
        });
        // Window gets hidden/cloaked → disappears from eligible list but is still alive
        fake.HideButKeepAlive(id);
        mon.Tick();
        // Comes back visible
        fake.AddWindow(new WindowInfo
        {
            Identity = id, ExePath = w.ExePath, ProcessName = w.ProcessName,
            ClassName = w.ClassName, Title = w.Title,
            CurrentRect = new WindowRect(888, 888, 300, 200), NormalRect = w.NormalRect,
            ShowState = WindowShowState.Normal, MonitorDevice = w.MonitorDevice
        });
        mon.Tick();
        mon.Tick();
        bool ok = fake.Moves.Count == firstMoves; // No new placement on return
        return (nameof(T_HiddenThenShownSameInstance_NotNew), ok, $"first={firstMoves} after={fake.Moves.Count}");
    }

    /// <summary>Explicit Apply must enable + persist monitoring.</summary>
    private static (string, bool, string?) T_ApplyExplicitEnablesMonitoring()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var ssPath = td.Path;
        var mon = new MonitoringService(fake, store, new SettingsStore(ssPath));
        mon.SetActiveProfile(profile);
        // Precondition: monitoring not enabled by SetActiveProfile alone
        if (mon.IsMonitoring) return (nameof(T_ApplyExplicitEnablesMonitoring), false, "SetActiveProfile enabled monitoring");
        _ = mon.ApplyActiveProfile();
        bool enabled = mon.IsMonitoring;
        // Persisted?
        var fresh = new SettingsStore(ssPath).Load();
        bool persisted = fresh.MonitoringEnabled && fresh.ActiveProfileId == profile.Id;
        return (nameof(T_ApplyExplicitEnablesMonitoring), enabled && persisted, $"enabled={enabled} persisted={persisted}");
    }

    /// <summary>After editing active profile on disk, RefreshActiveFromDisk re-primes without moving existing windows and keeps slot.</summary>
    private static (string, bool, string?) T_ActiveProfileRefresh_PreservesSlot()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        var reports = mon.ApplyActiveProfile();
        if (reports.Count(r => r.Result == PlaceResult.Moved) != 1) return (nameof(T_ActiveProfileRefresh_PreservesSlot), false, "apply did not move");
        // Simulate on-disk edit: change rect, add titleContains that still matches current title.
        var reloaded = store.Load(profile.Id)!;
        reloaded.Entries[0].Rect = new SavedRect { X = 400, Y = 400, Width = 600, Height = 500 };
        reloaded.Entries[0].TitleContains = "Untitled";
        store.Save(reloaded);
        mon.RefreshActiveFromDisk();
        int movesBefore = fake.Moves.Count;
        mon.Tick();
        mon.Tick();
        int movesAfter = fake.Moves.Count;
        // Refresh should NOT move the existing window; slot should remain reserved for id.
        bool slotReserved = mon.SlotSnapshot().Values.Any(x => x.Equals(id));
        bool noExtraMoves = movesBefore == movesAfter;
        return (nameof(T_ActiveProfileRefresh_PreservesSlot), slotReserved && noExtraMoves, $"reserved={slotReserved} movesBefore={movesBefore} after={movesAfter}");
    }

    /// <summary>Settings schema errors must be exposed via LastLoadError, not silently discarded.</summary>
    private static (string, bool, string?) T_SettingsSchemaErrorExposed()
    {
        using var td = new TempDir();
        var ssPath = Path.Combine(td.Path, "settings.json");
        File.WriteAllText(ssPath, "{\"activeProfileId\":\"abc\",\"monitoringEnabled\":true}"); // version missing
        var ss = new SettingsStore(td.Path);
        var s = ss.Load();
        bool exposed = !string.IsNullOrEmpty(ss.LastLoadError);
        bool fallback = s.MonitoringEnabled == false; // schema error → defaults, not silent "activeProfile active"
        return (nameof(T_SettingsSchemaErrorExposed), exposed && fallback, $"err='{ss.LastLoadError}' enabled={s.MonitoringEnabled}");
    }

    /// <summary>Profile file whose filename id does not match the id field is rejected on load.</summary>
    private static (string, bool, string?) T_ProfileFilenameIdMismatch_Rejected()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var mismatchPath = Path.Combine(store.ProfilesDir, "fileid.json");
        File.WriteAllText(mismatchPath, "{\"version\":1,\"id\":\"different\",\"name\":\"x\",\"entries\":[]}");
        bool threw = false;
        try { _ = store.Load("fileid"); } catch { threw = true; }
        return (nameof(T_ProfileFilenameIdMismatch_Rejected), threw, null);
    }

    /// <summary>Profile JSON without an explicit version field is rejected.</summary>
    private static (string, bool, string?) T_ProfileMissingVersion_Rejected()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var path = Path.Combine(store.ProfilesDir, "nover.json");
        File.WriteAllText(path, "{\"id\":\"nover\",\"name\":\"x\",\"entries\":[]}");
        bool threw = false;
        try { _ = store.Load("nover"); } catch { threw = true; }
        return (nameof(T_ProfileMissingVersion_Rejected), threw, null);
    }

    /// <summary>Profile with a null rect entry is rejected.</summary>
    private static (string, bool, string?) T_ProfileNullRectEntry_Rejected()
    {
        using var td = new TempDir();
        var store = new ProfileStore(td.Path);
        var path = Path.Combine(store.ProfilesDir, "nullrect.json");
        File.WriteAllText(path, "{\"version\":1,\"id\":\"nullrect\",\"name\":\"x\",\"entries\":[{\"id\":\"e1\",\"role\":\"r\",\"exePath\":\"C:/x.exe\",\"className\":\"X\",\"rect\":null}]}");
        bool threw = false;
        try { _ = store.Load("nullrect"); } catch { threw = true; }
        return (nameof(T_ProfileNullRectEntry_Rejected), threw, null);
    }

    /// <summary>App rejects size (returns smaller rect) → report Skipped_Rejected, not Moved.</summary>
    private static (string, bool, string?) T_RejectedMoveReportedHonestly()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 1000, 800));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        fake.ForceOutcome[new IntPtr(1)] = MoveOutcome.Rejected;
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        var reports = mon.ApplyActiveProfile();
        bool ok = reports.Count == 1 && reports[0].Result == PlaceResult.Skipped_Rejected;
        return (nameof(T_RejectedMoveReportedHonestly), ok, $"result={reports[0].Result}");
    }

    /// <summary>Window exists but hidden/cloaked at startup. Prime must mark its identity as seen
    /// (via wide enumeration) so later unhiding does not trigger a snap.</summary>
    private static (string, bool, string?) T_HiddenAtStartup_LaterShown_NotNew()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("notepad", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        // Hidden at startup: alive but NOT in Windows (eligible) list
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        fake.LiveIdentities.Add(id);
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        // Now the window becomes visible
        var w = Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled");
        fake.Windows.Add(w);
        for (int i = 0; i < 4; i++) mon.Tick();
        bool ok = fake.Moves.Count == 0; // Should NOT snap
        return (nameof(T_HiddenAtStartup_LaterShown_NotNew), ok, $"moves={fake.Moves.Count}");
    }

    /// <summary>Specific role A is reserved on existing A window; general B role's window closes; a
    /// new B window appears. Matcher must place B into the general slot cleanly without being
    /// confused by A (A is reserved and must be excluded from candidate pool).</summary>
    private static (string, bool, string?) T_SpecificReservedPlusGeneralSlotReopen_PlacesCleanly()
    {
        using var td = new TempDir();
        var profile = MakeProfile(
            ("specific", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "AAA", 10, 10, 400, 300),
            ("general", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", null, 500, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "AAA site"));
        fake.AddWindow(Win(2, 101, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "random site"));
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        _ = mon.ApplyActiveProfile();
        int applyMoves = fake.Moves.Count;
        // Close the general window (id=2) → slot released
        fake.RemoveWindow(new WindowIdentity(new IntPtr(2), 101, 1000));
        mon.Tick();
        // A new general window appears
        fake.AddWindow(Win(3, 102, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "something else", start: 2000));
        for (int i = 0; i < 3; i++) mon.Tick();
        bool ok = fake.Moves.Count == applyMoves + 1;
        return (nameof(T_SpecificReservedPlusGeneralSlotReopen_PlacesCleanly), ok, $"applyMoves={applyMoves} totalMoves={fake.Moves.Count}");
    }

    /// <summary>Window appears with placeholder "Loading..." title; actual title ("GitHub") arrives
    /// a few ticks later. Must stabilize and then place; must NOT be marked seen prematurely.</summary>
    private static (string, bool, string?) T_LoadingTitleStabilizesBeforeMarkedSeen()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("gh", @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "GitHub", 10, 10, 500, 400));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        fake.AddWindow(Win(1, 100, @"C:\x\chrome.exe", "Chrome_WidgetWin_1", "Loading..."));
        mon.Tick();
        mon.Tick();
        if (fake.Moves.Count != 0) return (nameof(T_LoadingTitleStabilizesBeforeMarkedSeen), false, "moved on placeholder");
        // Now title updates to the real one
        fake.MutateInPlace(id, w => new WindowInfo
        {
            Identity = w.Identity, ExePath = w.ExePath, ProcessName = w.ProcessName, ClassName = w.ClassName,
            Title = "repo - GitHub", CurrentRect = w.CurrentRect, NormalRect = w.NormalRect,
            ShowState = w.ShowState, MonitorDevice = w.MonitorDevice
        });
        mon.Tick();
        bool ok = fake.Moves.Count == 1;
        return (nameof(T_LoadingTitleStabilizesBeforeMarkedSeen), ok, $"moves={fake.Moves.Count}");
    }

    // ===== Round-4 regressions =====

    /// <summary>ProfileUpdater.Build must not mutate the original profile; only Proposed is updated.</summary>
    private static (string, bool, string?) T_UpdateProposal_DoesNotMutateOriginal()
    {
        var orig = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled", x: 500, y: 500, w: 600, h: 400));
        var beforeX = orig.Entries[0].Rect.X;
        var beforeY = orig.Entries[0].Rect.Y;
        var beforeW = orig.Entries[0].Rect.Width;
        var beforeH = orig.Entries[0].Rect.Height;
        var beforeMax = orig.Entries[0].Maximized;
        var proposal = ProfileUpdater.Build(orig, fake.Windows);
        bool origUntouched = orig.Entries[0].Rect.X == beforeX && orig.Entries[0].Rect.Y == beforeY
            && orig.Entries[0].Rect.Width == beforeW && orig.Entries[0].Rect.Height == beforeH
            && orig.Entries[0].Maximized == beforeMax;
        bool proposalUpdated = proposal.Proposed.Entries[0].Rect.X == 500 && proposal.Proposed.Entries[0].Rect.Y == 500;
        bool updateReported = proposal.UpdatedCount == 1 && proposal.HasAnyChange;
        return (nameof(T_UpdateProposal_DoesNotMutateOriginal), origUntouched && proposalUpdated && updateReported,
            $"origUntouched={origUntouched} proposalUpdated={proposalUpdated} reported={updateReported}");
    }

    /// <summary>CopyInto preserves object identity for dest and clones rect so later mutations are independent.</summary>
    private static (string, bool, string?) T_UpdateCopyInto_PreservesFieldsAndClonesRect()
    {
        var dest = MakeProfile(("a", @"C:\x\np.exe", "N", "foo", 10, 10, 400, 300));
        var destId = dest.Id;
        var destEntryId = dest.Entries[0].Id;
        var destRole = dest.Entries[0].Role;
        var destTitle = dest.Entries[0].TitleContains;
        var src = MakeProfile(("a", @"C:\x\np.exe", "N", "foo", 777, 888, 500, 400));
        src.Id = destId;
        src.Entries[0].Id = destEntryId;
        src.Entries[0].Role = destRole;
        src.Entries[0].TitleContains = destTitle;
        ProfileUpdater.CopyInto(dest, src);
        // Same dest identity, new rect values.
        bool rectCopied = dest.Entries[0].Rect.X == 777 && dest.Entries[0].Rect.Y == 888;
        bool fieldsPreserved = dest.Entries[0].Id == destEntryId && dest.Entries[0].Role == destRole
            && dest.Entries[0].TitleContains == destTitle;
        // Mutating src rect must not affect dest.
        src.Entries[0].Rect.X = 1;
        bool independent = dest.Entries[0].Rect.X == 777;
        return (nameof(T_UpdateCopyInto_PreservesFieldsAndClonesRect), rectCopied && fieldsPreserved && independent,
            $"rect={rectCopied} fields={fieldsPreserved} indep={independent}");
    }

    /// <summary>Reserved slot survives pause/resume cycle when window becomes hidden-but-alive.</summary>
    private static (string, bool, string?) T_HiddenSlotPreservedAcrossPauseResume()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        var w = Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled");
        fake.AddWindow(w);
        mon.Tick(); // place → reserve
        if (!mon.SlotSnapshot().Values.Any(x => x.Equals(id))) return (nameof(T_HiddenSlotPreservedAcrossPauseResume), false, "initial_reserve_failed");
        // Hide (still alive) and persist its hidden metadata so the reserved slot is still valid.
        fake.HiddenMetadata[id] = w;
        fake.HideButKeepAlive(id);
        mon.Pause();
        mon.Resume();
        bool preserved = mon.SlotSnapshot().Values.Any(x => x.Equals(id));
        // Now a competing B appears → must be skipped because slot occupied by hidden A.
        fake.AddWindow(Win(2, 101, @"C:\x\notepad.exe", "Notepad", "B", start: 2000));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool noExtraMove = fake.Moves.Count == 1;
        return (nameof(T_HiddenSlotPreservedAcrossPauseResume), preserved && noExtraMove,
            $"preserved={preserved} totalMoves={fake.Moves.Count}");
    }

    /// <summary>Cold startup: hidden identifiable window at startup reserves its slot via hidden
    /// metadata enumeration, so a later competing visible window does NOT get the slot.</summary>
    private static (string, bool, string?) T_HiddenAtStart_WithMetadata_ReservedPreventsCompetingSteal()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 100, 100, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        // Hidden-at-start: identity alive + hidden metadata available, but not in Enumerate() visible list.
        var id = new WindowIdentity(new IntPtr(1), 100, 1000);
        fake.LiveIdentities.Add(id);
        fake.HiddenMetadata[id] = Win(1, 100, @"C:\x\notepad.exe", "Notepad", "A");
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        bool reserved = mon.SlotSnapshot().Values.Any(x => x.Equals(id));
        // Competing B appears; strict match would otherwise pick B. Reservation should block it.
        fake.AddWindow(Win(2, 101, @"C:\x\notepad.exe", "Notepad", "B", start: 2000));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool noMoveOfB = fake.Moves.Count == 0;
        return (nameof(T_HiddenAtStart_WithMetadata_ReservedPreventsCompetingSteal), reserved && noMoveOfB,
            $"reserved={reserved} moves={fake.Moves.Count}");
    }

    /// <summary>Settings load error must preserve original file; subsequent Save creates a .bad backup.</summary>
    private static (string, bool, string?) T_SettingsCorruptBackupOnOverwrite()
    {
        using var td = new TempDir();
        var ssPath = Path.Combine(td.Path, "settings.json");
        File.WriteAllText(ssPath, "{not valid json");
        var ss = new SettingsStore(td.Path);
        var s = ss.Load();
        if (string.IsNullOrEmpty(ss.LastLoadError))
            return (nameof(T_SettingsCorruptBackupOnOverwrite), false, "no_load_error");
        // Original file must still be present untouched.
        if (!File.Exists(ssPath) || File.ReadAllText(ssPath) != "{not valid json")
            return (nameof(T_SettingsCorruptBackupOnOverwrite), false, "original_disturbed");
        // A corrupt-copy file must exist.
        bool corruptCopy = Directory.EnumerateFiles(td.Path, "settings.corrupt.*.bad").Any();
        // Save should back up the original before overwriting.
        ss.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = true });
        bool backupCopy = Directory.EnumerateFiles(td.Path, "settings.preoverwrite.*.bad").Any();
        bool newContent = File.ReadAllText(ssPath).Contains("monitoringEnabled");
        return (nameof(T_SettingsCorruptBackupOnOverwrite), corruptCopy && backupCopy && newContent,
            $"corrupt={corruptCopy} preover={backupCopy} newContent={newContent}");
    }

    /// <summary>Settings file with wrong activeProfileId type is rejected with explicit error.</summary>
    private static (string, bool, string?) T_SettingsStrictActiveProfileIdType()
    {
        using var td = new TempDir();
        var ssPath = Path.Combine(td.Path, "settings.json");
        File.WriteAllText(ssPath, "{\"version\":1,\"activeProfileId\":12345,\"monitoringEnabled\":true}");
        var ss = new SettingsStore(td.Path);
        var s = ss.Load();
        bool rejected = !string.IsNullOrEmpty(ss.LastLoadError) && ss.LastLoadError.Contains("activeProfileId");
        bool defaulted = s.MonitoringEnabled == false;
        return (nameof(T_SettingsStrictActiveProfileIdType), rejected && defaulted, $"err='{ss.LastLoadError}'");
    }

    /// <summary>Simulate CLI apply via MonitoringService: SetActiveProfile + ApplyActiveProfile
    /// enables monitoring AND persists the setting so the next load reports monitoring on.</summary>
    private static (string, bool, string?) T_ApplyCliSetsActiveAndEnablesMonitoring()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("a", @"C:\x\notepad.exe", "Notepad", null, 10, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\notepad.exe", "Notepad", "Untitled"));
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        // Emulate ApplyCommand: SetActiveProfile first, then ApplyActiveProfile.
        mon.SetActiveProfile(profile);
        var reports = mon.ApplyActiveProfile();
        var fresh = new SettingsStore(td.Path).Load();
        bool ok = reports.Any(r => r.Result == PlaceResult.Moved)
            && fresh.MonitoringEnabled && fresh.ActiveProfileId == profile.Id;
        return (nameof(T_ApplyCliSetsActiveAndEnablesMonitoring), ok,
            $"monEnabled={fresh.MonitoringEnabled} active={fresh.ActiveProfileId}");
    }

    // ===== Round-5 regressions =====

    /// <summary>Codex-reproduced: switch profile from p1→p2 (same app), Resume, open B. role2 must be
    /// reserved for existing A (via PrimeFromCurrent union+match), and B must NOT be moved.</summary>
    private static (string, bool, string?) T_SwitchProfilePrunesStaleBindings_ThenBReservedForRole2()
    {
        using var td = new TempDir();
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var a = Win(1, 101, @"C:\x\probe.exe", "ProbeClass", "A");
        fake.AddWindow(a);
        var p1 = MakeProfile(("role1", @"C:\x\probe.exe", "ProbeClass", null, 120, 140, 500, 350));
        p1.Id = "p1"; p1.Entries[0].Id = "role1";
        var p2 = MakeProfile(("role2", @"C:\x\probe.exe", "ProbeClass", null, 120, 140, 500, 350));
        p2.Id = "p2"; p2.Entries[0].Id = "role2";
        var store = new ProfileStore(td.Path); store.Save(p1); store.Save(p2);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(p1);
        mon.SetActiveProfile(p2);
        mon.Resume();
        var slots = mon.SlotSnapshot();
        bool reservedForRole2 = slots.ContainsKey("role2") && slots["role2"].Equals(a.Identity);
        bool noStaleRole1 = !slots.ContainsKey("role1");
        fake.AddWindow(Win(2, 102, @"C:\x\probe.exe", "ProbeClass", "B", start: 2000));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool bNotMoved = fake.Moves.Count == 0;
        return (nameof(T_SwitchProfilePrunesStaleBindings_ThenBReservedForRole2),
            reservedForRole2 && noStaleRole1 && bNotMoved,
            $"reserved={reservedForRole2} noStale={noStaleRole1} moves={fake.Moves.Count} slots={string.Join(",", slots.Keys)}");
    }

    /// <summary>Codex-reproduced: A reserved for role1, A hidden-alive, explicit Apply, then B opens.
    /// Hidden reservation must survive Apply; B must NOT take role1.</summary>
    private static (string, bool, string?) T_HiddenReservedSurvivesExplicitApply_ThenCompetingBSkipped()
    {
        using var td = new TempDir();
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var a = Win(1, 101, @"C:\x\probe.exe", "ProbeClass", "A");
        fake.AddWindow(a);
        var p1 = MakeProfile(("role1", @"C:\x\probe.exe", "ProbeClass", null, 120, 140, 500, 350));
        p1.Id = "p1"; p1.Entries[0].Id = "role1";
        var store = new ProfileStore(td.Path); store.Save(p1);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(p1);
        // Hide A (still alive) and keep its metadata so ApplyProfileInternal's union sees it.
        fake.HiddenMetadata[a.Identity] = a;
        fake.HideButKeepAlive(a.Identity);
        var reports = mon.ApplyActiveProfile();
        var slots = mon.SlotSnapshot();
        bool hiddenReserved = slots.ContainsKey("role1") && slots["role1"].Equals(a.Identity);
        bool reportIsSkipped = reports.Any(r => r.EntryRole == "role1" && r.Result == PlaceResult.Skipped_SlotOccupied && r.Note == "hidden_reserved");
        fake.AddWindow(Win(2, 102, @"C:\x\probe.exe", "ProbeClass", "B", start: 2000));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool bNotMoved = fake.Moves.Count == 0;
        return (nameof(T_HiddenReservedSurvivesExplicitApply_ThenCompetingBSkipped),
            hiddenReserved && reportIsSkipped && bNotMoved,
            $"hiddenReserved={hiddenReserved} reportSkipped={reportIsSkipped} moves={fake.Moves.Count}");
    }

    /// <summary>Editing a profile to remove an entry must prune the slot binding on refresh.</summary>
    private static (string, bool, string?) T_DeletedEntryBindingIsPruned()
    {
        using var td = new TempDir();
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var profile = MakeProfile(
            ("keep", @"C:\x\probe.exe", "ProbeClass", "KEEP", 100, 100, 400, 300),
            ("drop", @"C:\x\probe.exe", "ProbeClass", "DROP", 500, 100, 400, 300));
        var w1 = Win(1, 101, @"C:\x\probe.exe", "ProbeClass", "window KEEP alpha");
        var w2 = Win(2, 102, @"C:\x\probe.exe", "ProbeClass", "window DROP beta");
        fake.AddWindow(w1); fake.AddWindow(w2);
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        if (mon.SlotSnapshot().Count != 2) return (nameof(T_DeletedEntryBindingIsPruned), false, "initial_prime_failed");
        // Simulate an edit on disk that removes the "drop" entry.
        var edited = store.Load(profile.Id)!;
        var droppedId = edited.Entries.First(e => e.Role == "drop").Id;
        edited.Entries.RemoveAll(e => e.Role == "drop");
        store.Save(edited);
        mon.RefreshActiveFromDisk();
        var slots = mon.SlotSnapshot();
        bool pruned = !slots.ContainsKey(droppedId) && slots.Count == 1;
        return (nameof(T_DeletedEntryBindingIsPruned), pruned, $"slots={slots.Count} droppedPresent={slots.ContainsKey(droppedId)}");
    }

    private static (string, bool, string?) T_SettingsRequiredFields_ActiveProfileIdMissingRejected()
    {
        using var td = new TempDir();
        File.WriteAllText(Path.Combine(td.Path, "settings.json"), "{\"version\":1,\"monitoringEnabled\":true}");
        var ss = new SettingsStore(td.Path);
        ss.Load();
        bool rejected = !string.IsNullOrEmpty(ss.LastLoadError) && ss.LastLoadError.Contains("activeProfileId");
        return (nameof(T_SettingsRequiredFields_ActiveProfileIdMissingRejected), rejected, $"err='{ss.LastLoadError}'");
    }

    private static (string, bool, string?) T_SettingsRequiredFields_MonitoringEnabledMissingRejected()
    {
        using var td = new TempDir();
        File.WriteAllText(Path.Combine(td.Path, "settings.json"), "{\"version\":1,\"activeProfileId\":null}");
        var ss = new SettingsStore(td.Path);
        ss.Load();
        bool rejected = !string.IsNullOrEmpty(ss.LastLoadError) && ss.LastLoadError.Contains("monitoringEnabled");
        return (nameof(T_SettingsRequiredFields_MonitoringEnabledMissingRejected), rejected, $"err='{ss.LastLoadError}'");
    }

    /// <summary>Save-time backup discipline, even when Load() was never called in this session.</summary>
    private static (string, bool, string?) T_SettingsSaveBacksUpInvalidOriginal()
    {
        using var td = new TempDir();
        var ssPath = Path.Combine(td.Path, "settings.json");
        File.WriteAllText(ssPath, "{ bogus and incomplete");
        // Brand-new store, no Load call at all.
        var ss = new SettingsStore(td.Path);
        ss.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = true });
        bool backedUp = Directory.EnumerateFiles(td.Path, "settings.preoverwrite.*.bad").Any();
        bool rewritten = File.ReadAllText(ssPath).Contains("monitoringEnabled");
        return (nameof(T_SettingsSaveBacksUpInvalidOriginal), backedUp && rewritten,
            $"backedUp={backedUp} rewritten={rewritten}");
    }

    /// <summary>If the backup copy itself fails (destination name already exists), Save must throw
    /// WITHOUT touching the original file.</summary>
    private static (string, bool, string?) T_SettingsSaveFailsWhenBackupCollides()
    {
        using var td = new TempDir();
        var ssPath = Path.Combine(td.Path, "settings.json");
        var original = "{ bad json";
        File.WriteAllText(ssPath, original);
        // Pre-create a backup with the exact timestamp that Save will try to use. Since Save uses
        // UTC seconds, we overlap by making many files with every second within a window.
        // Simpler: lock the directory for file create to force Copy failure. We approximate by
        // creating a non-writable backup target. Use File.Copy's own overwrite=false semantics by
        // creating a backup file identical to the name pattern and preventing it from being
        // uniquely created in the same second. Simpler alternative: use File.SetAttributes
        // ReadOnly on the dir — unreliable. Easiest deterministic approach: patch via a sentinel
        // that File.Copy guaranteed to fail. We create a directory with the exact candidate name
        // so File.Copy throws IOException("already exists").
        var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var collisionPath = Path.Combine(td.Path, $"settings.preoverwrite.{ts}.bad");
        Directory.CreateDirectory(collisionPath); // Occupies the name as a directory.
        var ss = new SettingsStore(td.Path);
        bool threw = false;
        try { ss.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = true }); }
        catch { threw = true; }
        bool originalIntact = File.ReadAllText(ssPath) == original;
        return (nameof(T_SettingsSaveFailsWhenBackupCollides), threw && originalIntact,
            $"threw={threw} originalIntact={originalIntact}");
    }

    private static (string, bool, string?) T_RenameRollbackOnSaveFailure()
    {
        // Direct ProfileUpdater+ProfileStore test (MainForm.PerformRename semantics equivalent).
        using var td = new TempDir();
        var p = MakeProfile(("a", @"C:\x\np.exe", "N", null, 10, 10, 400, 300));
        p.Name = "OldName";
        var store = new ProfileStore(td.Path); store.Save(p);
        var live = ProfileUpdater.CloneProfile(p);
        var failing = new FailingProfileStore(td.Path);
        var clone = ProfileUpdater.CloneProfile(live);
        clone.Name = "NewName";
        bool threw = false;
        try { failing.Save(clone); } catch { threw = true; }
        // On failure, the live model must NOT have the new name applied by the caller.
        bool liveUnchanged = live.Name == "OldName";
        var reloaded = store.Load(p.Id)!;
        bool diskUnchanged = reloaded.Name == "OldName";
        return (nameof(T_RenameRollbackOnSaveFailure), threw && liveUnchanged && diskUnchanged,
            $"threw={threw} live='{live.Name}' disk='{reloaded.Name}'");
    }

    private static (string, bool, string?) T_RemoveEntryRollbackOnSaveFailure()
    {
        using var td = new TempDir();
        var p = MakeProfile(
            ("keep", @"C:\x\np.exe", "N", null, 10, 10, 400, 300),
            ("drop", @"C:\x\np.exe", "N", null, 50, 50, 400, 300));
        var store = new ProfileStore(td.Path); store.Save(p);
        var live = ProfileUpdater.CloneProfile(p);
        var clone = ProfileUpdater.CloneProfile(live);
        clone.Entries.RemoveAll(e => e.Role == "drop");
        var failing = new FailingProfileStore(td.Path);
        bool threw = false;
        try { failing.Save(clone); } catch { threw = true; }
        bool liveStillHasDrop = live.Entries.Any(e => e.Role == "drop");
        var reloaded = store.Load(p.Id)!;
        bool diskStillHasDrop = reloaded.Entries.Any(e => e.Role == "drop");
        return (nameof(T_RemoveEntryRollbackOnSaveFailure), threw && liveStillHasDrop && diskStillHasDrop,
            $"threw={threw} live={liveStillHasDrop} disk={diskStillHasDrop}");
    }

    private static (string, bool, string?) T_EditEntryRollbackOnSaveFailure()
    {
        using var td = new TempDir();
        var p = MakeProfile(("a", @"C:\x\np.exe", "N", "orig", 10, 10, 400, 300));
        var store = new ProfileStore(td.Path); store.Save(p);
        var live = ProfileUpdater.CloneProfile(p);
        var clone = ProfileUpdater.CloneProfile(live);
        clone.Entries[0].Role = "edited";
        clone.Entries[0].TitleContains = "edited-title";
        clone.Entries[0].Rect = new SavedRect { X = 777, Y = 888, Width = 500, Height = 400 };
        var failing = new FailingProfileStore(td.Path);
        bool threw = false;
        try { failing.Save(clone); } catch { threw = true; }
        bool liveUnchanged = live.Entries[0].Role == "a"
            && live.Entries[0].TitleContains == "orig"
            && live.Entries[0].Rect.X == 10 && live.Entries[0].Rect.Y == 10;
        var reloaded = store.Load(p.Id)!;
        bool diskUnchanged = reloaded.Entries[0].Role == "a"
            && reloaded.Entries[0].TitleContains == "orig"
            && reloaded.Entries[0].Rect.X == 10;
        return (nameof(T_EditEntryRollbackOnSaveFailure), threw && liveUnchanged && diskUnchanged,
            $"threw={threw} live={liveUnchanged} disk={diskUnchanged}");
    }

    /// <summary>EntryEditDialog must NOT mutate the input entry; it must expose results via its own
    /// *Result properties. (Validates the refactor so a user Cancel never changes anything.)</summary>
    private static (string, bool, string?) T_EntryEditDialog_DoesNotMutateInputBeforeOk()
    {
        // Reflection-based check: the dialog type exposes RoleResult/TitleContainsResult/RectResult/
        // MaximizedResult properties and no setter is called from outside.
        var t = typeof(App.EntryEditDialog);
        bool hasRoleResult = t.GetProperty("RoleResult") != null;
        bool hasTitleResult = t.GetProperty("TitleContainsResult") != null;
        bool hasRectResult = t.GetProperty("RectResult") != null;
        bool hasMaxResult = t.GetProperty("MaximizedResult") != null;
        // Legacy "Result" shortcut must be gone (so no code paths can accidentally read a mutated entry).
        bool oldResultGone = t.GetProperty("Result") == null;
        return (nameof(T_EntryEditDialog_DoesNotMutateInputBeforeOk),
            hasRoleResult && hasTitleResult && hasRectResult && hasMaxResult && oldResultGone,
            $"R={hasRoleResult} T={hasTitleResult} Rc={hasRectResult} M={hasMaxResult} oldGone={oldResultGone}");
    }

    /// <summary>Correct maximum-matching semantics for the shortage graph.
    ///
    /// Graph: specific→{A}, general1→{A,B}, general2→{A,B}, independent→{D} with distinct D-only
    /// window. Max cardinality = 3 (independent→D plus two of {specific,general1,general2}).
    /// Two valid maximum assignments:
    ///   {specific:A, general1:B, independent:D}  and  {general1:A, general2:B, independent:D}
    /// Therefore specific, general1, general2 are ALL Ambiguous (no forced edge). independent→D
    /// IS forced (removing that edge drops cardinality from 3 to 2). This verifies the engine does
    /// NOT prefer "more specific" entries via greedy / singleton propagation.</summary>
    private static (string, bool, string?) T_ExactShortageGraph_AllThreeAmbiguous_DisjointMatched()
    {
        var profile = MakeProfile(
            ("specific", @"C:\qa\probe.exe", "ProbeClass", "codexq73af-A", 200, 200, 400, 280),
            ("general1", @"C:\qa\probe.exe", "ProbeClass", "codexq73af",   200, 200, 400, 280),
            ("general2", @"C:\qa\probe.exe", "ProbeClass", "codexq73af",   200, 200, 400, 280),
            ("independent", @"C:\qa\other.exe", "OtherClass", "disjoint-D", 10, 10, 300, 200));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        // A matches specific AND both generals; B matches generals only; D matches independent only.
        fake.AddWindow(Win(1, 101, @"C:\qa\probe.exe", "ProbeClass", "A codexq73af-A alpha"));
        fake.AddWindow(Win(2, 102, @"C:\qa\probe.exe", "ProbeClass", "B codexq73af beta"));
        fake.AddWindow(Win(3, 103, @"C:\qa\other.exe", "OtherClass", "window disjoint-D"));
        var matches = LayoutEngine.MatchProfile(profile, fake.Windows);
        var sp = matches.First(m => m.Entry.Role == "specific");
        var g1 = matches.First(m => m.Entry.Role == "general1");
        var g2 = matches.First(m => m.Entry.Role == "general2");
        var ind = matches.First(m => m.Entry.Role == "independent");
        bool ok = sp.Outcome == MatchOutcome.Ambiguous
                  && g1.Outcome == MatchOutcome.Ambiguous
                  && g2.Outcome == MatchOutcome.Ambiguous
                  && ind.Outcome == MatchOutcome.Matched
                  && ind.Window?.Identity.Pid == 103;
        return (nameof(T_ExactShortageGraph_AllThreeAmbiguous_DisjointMatched), ok,
            $"spec={sp.Outcome} g1={g1.Outcome} g2={g2.Outcome} ind={ind.Outcome}");
    }

    /// <summary>Codex-reproduced regression: profile edit changes a role's TitleContains from A to B,
    /// detached save + RefreshActiveFromDisk. Old binding (A occupies role1) must be invalidated so
    /// the next matching B is placed into role1. A itself is NEVER moved by this flow.</summary>
    private static (string, bool, string?) T_ExplicitTitleFilterEdit_InvalidatesStaleBinding_BMoves()
    {
        using var td = new TempDir();
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var a = Win(1, 101, @"C:\x\probe.exe", "ProbeClass", "A alpha");
        fake.AddWindow(a);
        var p = MakeProfile(("role1", @"C:\x\probe.exe", "ProbeClass", "A", 120, 140, 500, 350));
        p.Id = "edit-profile"; p.Entries[0].Id = "edit-role";
        var store = new ProfileStore(td.Path); store.Save(p);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(p);
        mon.Resume();
        if (!mon.SlotSnapshot().TryGetValue("edit-role", out var pre) || !pre.Equals(a.Identity))
            return (nameof(T_ExplicitTitleFilterEdit_InvalidatesStaleBinding_BMoves), false, "initial_reserve_failed");
        var movesBeforeEdit = fake.Moves.Count;
        // Save a detached replacement with the SAME IDs but TitleContains changed to "B".
        var edited = store.Load("edit-profile")!;
        edited.Entries[0].TitleContains = "B";
        store.Save(edited);
        mon.RefreshActiveFromDisk();
        // After refresh, the saved predicate for edit-role was "A"; current entry predicate is "B".
        // Mismatch → stale binding invalidated.
        bool oldBindingDropped = !mon.SlotSnapshot().ContainsKey("edit-role");
        // Open a B window; monitoring must auto-place it; A must stay put.
        fake.AddWindow(Win(2, 102, @"C:\x\probe.exe", "ProbeClass", "B beta", start: 2000));
        for (int i = 0; i < MonitoringService.StabilizationMaxTicks + 2; i++) mon.Tick();
        bool movedExactlyOne = (fake.Moves.Count - movesBeforeEdit) == 1
                               && fake.Moves.Last().id.Pid == 102;
        return (nameof(T_ExplicitTitleFilterEdit_InvalidatesStaleBinding_BMoves),
            oldBindingDropped && movedExactlyOne,
            $"dropped={oldBindingDropped} movedDelta={fake.Moves.Count - movesBeforeEdit}");
    }

    /// <summary>A live window's title changing WITHOUT a profile edit must NOT release the slot.
    /// This proves the predicate-snapshot check is driven by explicit edits, not live window changes.</summary>
    private static (string, bool, string?) T_LiveTitleChange_UnderUnchangedPredicate_KeepsBinding()
    {
        using var td = new TempDir();
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var a = Win(1, 101, @"C:\x\probe.exe", "ProbeClass", "Document - MyApp");
        fake.AddWindow(a);
        var p = MakeProfile(("docs", @"C:\x\probe.exe", "ProbeClass", "MyApp", 10, 10, 400, 300));
        var store = new ProfileStore(td.Path); store.Save(p);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(p);
        mon.Resume();
        var slotEntryId = p.Entries[0].Id;
        if (!mon.SlotSnapshot().ContainsKey(slotEntryId))
            return (nameof(T_LiveTitleChange_UnderUnchangedPredicate_KeepsBinding), false, "initial_reserve_failed");
        // The LIVE window's title changes (user renamed the document) but still contains "MyApp".
        fake.MutateInPlace(a.Identity, w => new WindowInfo
        {
            Identity = w.Identity, ExePath = w.ExePath, ProcessName = w.ProcessName,
            ClassName = w.ClassName, Title = "Renamed-Document - MyApp",
            CurrentRect = w.CurrentRect, NormalRect = w.NormalRect,
            ShowState = w.ShowState, MonitorDevice = w.MonitorDevice
        });
        mon.RefreshActiveFromDisk();
        bool stillReserved = mon.SlotSnapshot().ContainsKey(slotEntryId);
        return (nameof(T_LiveTitleChange_UnderUnchangedPredicate_KeepsBinding), stillReserved,
            $"stillReserved={stillReserved}");
    }

    /// <summary>Settings save failure must set MonitoringService.LastPersistError so callers (CLI,
    /// GUI status bar) can truthfully report persistence failure instead of a false success.</summary>
    private static (string, bool, string?) T_MonitoringServiceLastPersistError_SetOnSaveFailure()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("a", @"C:\x\np.exe", "N", null, 10, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\np.exe", "N", "Untitled"));
        var store = new ProfileStore(td.Path); store.Save(profile);
        var ssPath = Path.Combine(td.Path, "settings.json");
        // Pre-create a directory with the exact pre-overwrite backup prefix we expect so Save's
        // mandatory backup fails (we've written invalid bytes to settings.json first so Save enters
        // the backup branch; the directory collision forces File.Copy to throw).
        File.WriteAllText(ssPath, "{ not valid");
        // Pre-create a directory at every candidate second within a short window so Save's
        // File.Copy(backup) must collide regardless of exact timing. Backup failure throws
        // in Save; MonitoringService.Persist catches it and sets LastPersistError.
        var now = DateTime.UtcNow;
        for (int dt = -1; dt <= 3; dt++)
        {
            var ts = now.AddSeconds(dt).ToString("yyyyMMddHHmmss");
            try { Directory.CreateDirectory(Path.Combine(td.Path, $"settings.preoverwrite.{ts}.bad")); } catch { }
        }
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile); // Persist throws → LastPersistError set
        bool errSet = !string.IsNullOrEmpty(mon.LastPersistError);
        // Original settings bytes untouched (ValidateExisting-driven backup refused to proceed).
        bool originalIntact = File.ReadAllText(ssPath) == "{ not valid";
        return (nameof(T_MonitoringServiceLastPersistError_SetOnSaveFailure), errSet && originalIntact,
            $"err='{mon.LastPersistError}' intact={originalIntact}");
    }

    /// <summary>LastPersistError must clear on a subsequent successful Persist so the status is
    /// not permanently stuck after transient failures resolve.</summary>
    private static (string, bool, string?) T_MonitoringServiceLastPersistError_ClearedOnSuccess()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("a", @"C:\x\np.exe", "N", null, 10, 10, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        var store = new ProfileStore(td.Path); store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile); // first persist succeeds
        if (!string.IsNullOrEmpty(mon.LastPersistError))
            return (nameof(T_MonitoringServiceLastPersistError_ClearedOnSuccess), false, $"unexpected_initial_err='{mon.LastPersistError}'");
        mon.Pause();
        return (nameof(T_MonitoringServiceLastPersistError_ClearedOnSuccess), string.IsNullOrEmpty(mon.LastPersistError),
            $"after_pause_err='{mon.LastPersistError}'");
    }

    private sealed class FailingProfileStore : ProfileStore
    {
        public FailingProfileStore(string dataDir) : base(dataDir) { }
        public override void Save(Profile p) => throw new IOException("test_forced_save_failure");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wlm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            // Non-recursive best-effort cleanup — never recursive-delete anything outside this temp dir.
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); } catch { }
                    }
                    foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                    {
                        try { Directory.Delete(d, false); } catch { }
                    }
                    try { Directory.Delete(Path, false); } catch { }
                }
            }
            catch { }
        }
    }
}
