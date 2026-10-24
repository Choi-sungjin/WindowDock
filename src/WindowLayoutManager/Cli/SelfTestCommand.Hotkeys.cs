using System.Text.Json.Nodes;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

/// <summary>
/// v0.2.0 regressions: hotkeys.json persistence/validation, registration commit/rollback using a
/// FAKE registrar (no OS hotkeys are registered by --self-test), Previous/Next ordering, and the
/// quick-apply service against the in-memory FakeProvider (no native windows).
/// </summary>
public static partial class SelfTestCommand
{
    private static IEnumerable<Func<(string, bool, string?)>> HotkeyRoundTests() => new Func<(string, bool, string?)>[]
    {
        T_Hk_DefaultsAllDisabledUnassigned,
        T_Hk_MissingFile_DefaultsWithoutWriting,
        T_Hk_SaveLoadRoundTrip,
        T_Hk_RenameKeepsBindingByStableId,
        T_Hk_CorruptFile_DefaultsAndPreservesBytesOnce,
        T_Hk_OversizeFileRejected,
        T_Hk_MalformedShapes_AllRejected,
        T_Hk_DuplicateEnabledComboRejected_DisabledDuplicatesAllowed,
        T_Hk_ComboRules,
        T_Hk_SaveBacksUpInvalidOriginal,
        T_Hk_LockedFileSave_PreservesMemoryAndRegistrations,
        T_Hk_InvalidCommit_ChangesNothing,
        T_Hk_CollisionReportedTruthfully,
        T_Hk_SwapCombos_NoSelfCollision,
        T_Hk_ProfileOnlyChange_DoesNotReRegister,
        T_Hk_Shutdown_UnregistersAll,
        T_Hk_ConfiguringDoesNotMoveWindows,
        T_Nav_EmptyListReturnsNull,
        T_Nav_NoAnchor_NextFirstPreviousLast,
        T_Nav_ActiveAnchor_WrapsBothDirections,
        T_Nav_FallsBackToSelectedWhenActiveMissing,
        T_Nav_SameNameTies_StableById,
        T_Nav_IncludesProfilesBeyondEightSlots,
        T_Quick_Unassigned_DoesNothing,
        T_Quick_DeletedId_NeverAppliesOther,
        T_Quick_CorruptFile_NeverAppliesOther,
        T_Quick_UnsafeId_Rejected,
        T_Quick_LoadsFreshFromDisk,
        T_Quick_Applied_SetsActiveEnablesAndPersistsMonitoring,
        // Round 22
        T_Hk_DuplicateRootProperty_MalformedNotCrash,
        T_Hk_DuplicateBindingProperty_MalformedNotCrash,
        T_Settings_DuplicateProperty_DefaultsNotCrash,
        T_Enumerate_OwnBlockedThreadWindow_NoTitleWait,
        T_CaptureBox_CaptureComboValidates,
        // Round 24 (v0.2.1)
        T_AppIcon_EmbeddedLoadsAllSizes,
        T_AppInfo_TitleHasAssemblyVersion,
        T_Activation_NormalSecondLaunchSignalsListenerOnce,
        T_Activation_DuplicateAutostartIsSilentAndSendsNothing,
        T_Activation_NoListenerReportsUnavailableQuickly,
    };

    // ---------- helpers ----------

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public readonly Dictionary<int, (uint mods, uint vk)> Registered = new();
        public readonly HashSet<(uint mods, uint vk)> ExternallyTaken = new();
        public int RegisterCalls;
        public int UnregisterCalls;

        public bool TryRegister(int id, uint modifiers, uint virtualKey, out int win32Error)
        {
            RegisterCalls++;
            var key = (modifiers & ~HotkeyRules.MOD_NOREPEAT, virtualKey);
            if (ExternallyTaken.Contains(key) || Registered.ContainsKey(id) || Registered.Values.Contains(key))
            {
                win32Error = HotkeyStatus.ERROR_HOTKEY_ALREADY_REGISTERED;
                return false;
            }
            Registered[id] = key;
            win32Error = 0;
            return true;
        }

        public void Unregister(int id)
        {
            UnregisterCalls++;
            Registered.Remove(id);
        }
    }

    private static (uint, uint) Combo(bool ctrl, bool alt, bool shift, Keys key) =>
        ((ctrl ? HotkeyRules.MOD_CONTROL : 0) | (alt ? HotkeyRules.MOD_ALT : 0) | (shift ? HotkeyRules.MOD_SHIFT : 0), (uint)key);

    private static Profile NamedProfile(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Entries =
        {
            new ProfileEntry
            {
                Id = "e-" + id, Role = "r", ExePath = @"C:\x\np.exe", ProcessName = "np",
                ClassName = "N", Rect = new SavedRect { X = 10, Y = 10, Width = 400, Height = 300 }
            }
        }
    };

    // ---------- hotkeys.json / config ----------

    private static (string, bool, string?) T_Hk_DefaultsAllDisabledUnassigned()
    {
        var c = HotkeyConfig.CreateDefault();
        bool ok = c.Bindings.Count == 10 && c.Validate().Count == 0 && c.Bindings.All(b => !b.Enabled);
        for (int i = 0; i < 8; i++)
        {
            var b = c.Get((HotkeyActionId)i);
            ok &= b.ProfileId == null && b.Ctrl && b.Alt && !b.Shift && b.Key == Keys.D1 + i;
        }
        var prev = c.Get(HotkeyActionId.Previous);
        var next = c.Get(HotkeyActionId.Next);
        ok &= prev.Ctrl && prev.Alt && prev.Key == Keys.Left && next.Ctrl && next.Alt && next.Key == Keys.Right;
        return (nameof(T_Hk_DefaultsAllDisabledUnassigned), ok, null);
    }

    private static (string, bool, string?) T_Hk_MissingFile_DefaultsWithoutWriting()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var c = store.Load(out var err);
        bool ok = err == null && c.ContentEquals(HotkeyConfig.CreateDefault()) && !File.Exists(store.FilePath)
                  && !Directory.EnumerateFiles(td.Path).Any();
        return (nameof(T_Hk_MissingFile_DefaultsWithoutWriting), ok, $"err={err} files={Directory.EnumerateFiles(td.Path).Count()}");
    }

    private static (string, bool, string?) T_Hk_SaveLoadRoundTrip()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var c = HotkeyConfig.CreateDefault();
        c.Get(HotkeyActionId.Slot1).ProfileId = "p1";
        c.Get(HotkeyActionId.Slot1).Enabled = true;
        c.Get(HotkeyActionId.Slot2).ProfileId = "p2";
        c.Get(HotkeyActionId.Slot2).Shift = true;
        c.Get(HotkeyActionId.Next).Enabled = true;
        c.Get(HotkeyActionId.Next).Key = Keys.PageDown;
        store.Save(c);
        var back = store.Load(out var err);
        bool ok = err == null && back.ContentEquals(c);
        return (nameof(T_Hk_SaveLoadRoundTrip), ok, $"err={err}");
    }

    private static (string, bool, string?) T_Hk_RenameKeepsBindingByStableId()
    {
        using var td = new TempDir();
        var profiles = new ProfileStore(td.Path);
        var p = NamedProfile("stable01", "Old name");
        profiles.Save(p);
        var store = new HotkeyStore(td.Path);
        var c = HotkeyConfig.CreateDefault();
        c.Get(HotkeyActionId.Slot3).ProfileId = p.Id;
        store.Save(c);
        var renamed = profiles.Load(p.Id)!;
        renamed.Name = "New name";
        profiles.Save(renamed);
        var back = store.Load(out var err);
        bool ok = err == null && back.Get(HotkeyActionId.Slot3).ProfileId == "stable01"
                  && profiles.Load("stable01")!.Name == "New name";
        return (nameof(T_Hk_RenameKeepsBindingByStableId), ok, $"slot3={back.Get(HotkeyActionId.Slot3).ProfileId}");
    }

    private static (string, bool, string?) T_Hk_CorruptFile_DefaultsAndPreservesBytesOnce()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var corrupt = "{ \"version\": 1, \"bindings\": [ oops";
        File.WriteAllText(store.FilePath, corrupt);
        var c1 = store.Load(out var err1);
        var c2 = store.Load(out var err2);
        var copies = Directory.EnumerateFiles(td.Path, "hotkeys.corrupt.*.bad").ToList();
        bool preserved = copies.Count == 1 && File.ReadAllText(copies[0]) == corrupt;
        bool originalIntact = File.ReadAllText(store.FilePath) == corrupt;
        bool ok = err1 != null && err2 != null && c1.ContentEquals(HotkeyConfig.CreateDefault())
                  && c2.Bindings.All(b => !b.Enabled) && preserved && originalIntact;
        return (nameof(T_Hk_CorruptFile_DefaultsAndPreservesBytesOnce), ok, $"copies={copies.Count} intact={originalIntact}");
    }

    private static (string, bool, string?) T_Hk_OversizeFileRejected()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        File.WriteAllText(store.FilePath, "{" + new string(' ', (int)HotkeyStore.MaxFileBytes + 10) + "}");
        var c = store.Load(out var err);
        bool ok = err != null && c.Bindings.All(b => !b.Enabled) && File.Exists(store.FilePath);
        return (nameof(T_Hk_OversizeFileRejected), ok, err);
    }

    private static (string, bool, string?) T_Hk_MalformedShapes_AllRejected()
    {
        var baseline = HotkeyStore.Serialize(HotkeyConfig.CreateDefault());
        JsonObject Root() => (JsonObject)JsonNode.Parse(baseline)!;
        JsonObject B(JsonObject r, int i) => (JsonObject)((JsonArray)r["bindings"]!)[i]!;

        var cases = new List<(string name, Func<JsonObject> make)>
        {
            ("missing_version", () => { var r = Root(); r.Remove("version"); return r; }),
            ("version_2", () => { var r = Root(); r["version"] = 2; return r; }),
            ("version_string", () => { var r = Root(); r["version"] = "1"; return r; }),
            ("bindings_not_array", () => { var r = Root(); r["bindings"] = new JsonObject(); return r; }),
            ("nine_bindings", () => { var r = Root(); ((JsonArray)r["bindings"]!).RemoveAt(9); return r; }),
            ("unknown_action", () => { var r = Root(); B(r, 0)["action"] = "slot9"; return r; }),
            ("duplicate_action", () => { var r = Root(); B(r, 1)["action"] = "slot1"; return r; }),
            ("enabled_as_string", () => { var r = Root(); B(r, 0)["enabled"] = "true"; return r; }),
            ("ctrl_missing", () => { var r = Root(); B(r, 0).Remove("ctrl"); return r; }),
            ("key_f12", () => { var r = Root(); B(r, 0)["key"] = "F12"; return r; }),
            ("key_lwin", () => { var r = Root(); B(r, 0)["key"] = "LWin"; return r; }),
            ("key_numeric", () => { var r = Root(); B(r, 0)["key"] = "49"; return r; }),
            ("key_flag_combo", () => { var r = Root(); B(r, 0)["key"] = "D1, Control"; return r; }),
            ("no_ctrl_no_alt", () => { var r = Root(); B(r, 0)["ctrl"] = false; B(r, 0)["alt"] = false; return r; }),
            ("win_true", () => { var r = Root(); B(r, 0)["win"] = true; return r; }),
            ("profile_traversal", () => { var r = Root(); B(r, 0)["profileId"] = "../evil"; return r; }),
            ("profile_number", () => { var r = Root(); B(r, 0)["profileId"] = 5; return r; }),
            ("previous_with_profile", () => { var r = Root(); B(r, 8)["profileId"] = "p1"; return r; }),
            ("unrelated_object", () => new JsonObject { ["x"] = new JsonArray() }),
        };
        var accepted = new List<string>();
        foreach (var (name, make) in cases)
        {
            try
            {
                HotkeyStore.ParseAndValidate(make().ToJsonString());
                accepted.Add(name);
            }
            catch (InvalidDataException) { }
            catch (Exception ex) { accepted.Add(name + ":" + ex.GetType().Name); }
        }
        foreach (var raw in new[] { "[]", "null", "42", "\"text\"", "" })
        {
            try { HotkeyStore.ParseAndValidate(raw); accepted.Add("raw:" + raw); }
            catch (InvalidDataException) { }
            catch (Exception ex) { accepted.Add("raw:" + raw + ":" + ex.GetType().Name); }
        }
        // Sanity: the untouched baseline must parse.
        bool baselineOk;
        try { HotkeyStore.ParseAndValidate(baseline); baselineOk = true; } catch { baselineOk = false; }
        return (nameof(T_Hk_MalformedShapes_AllRejected), accepted.Count == 0 && baselineOk,
            $"wronglyAccepted=[{string.Join(",", accepted)}] baselineOk={baselineOk}");
    }

    private static (string, bool, string?) T_Hk_DuplicateEnabledComboRejected_DisabledDuplicatesAllowed()
    {
        var c = HotkeyConfig.CreateDefault();
        c.Get(HotkeyActionId.Slot2).Key = Keys.D1; // same as slot1, both disabled
        bool disabledOk = c.Validate().Count == 0;
        c.Get(HotkeyActionId.Slot1).Enabled = true;
        bool oneEnabledOk = c.Validate().Count == 0;
        c.Get(HotkeyActionId.Slot2).Enabled = true;
        bool bothRejected = c.Validate().Count > 0;
        return (nameof(T_Hk_DuplicateEnabledComboRejected_DisabledDuplicatesAllowed), disabledOk && oneEnabledOk && bothRejected,
            $"disabled={disabledOk} one={oneEnabledOk} bothRejected={bothRejected}");
    }

    private static (string, bool, string?) T_Hk_ComboRules()
    {
        bool shiftOnlyRejected = HotkeyRules.ValidateCombo(false, false, true, Keys.A) != null;
        bool noModRejected = HotkeyRules.ValidateCombo(false, false, false, Keys.A) != null;
        bool f12Rejected = HotkeyRules.ValidateCombo(true, true, false, Keys.F12) != null;
        bool winRejected = HotkeyRules.ValidateCombo(true, true, false, Keys.LWin) != null;
        bool modOnlyRejected = HotkeyRules.ValidateCombo(true, false, false, Keys.ControlKey) != null;
        bool altArrowOk = HotkeyRules.ValidateCombo(false, true, false, Keys.Left) == null;
        bool ctrlShiftF11Ok = HotkeyRules.ValidateCombo(true, false, true, Keys.F11) == null;
        bool ok = shiftOnlyRejected && noModRejected && f12Rejected && winRejected && modOnlyRejected && altArrowOk && ctrlShiftF11Ok;
        return (nameof(T_Hk_ComboRules), ok,
            $"shiftOnly={shiftOnlyRejected} noMod={noModRejected} f12={f12Rejected} win={winRejected} modOnly={modOnlyRejected} altArrow={altArrowOk} ctrlShiftF11={ctrlShiftF11Ok}");
    }

    private static (string, bool, string?) T_Hk_SaveBacksUpInvalidOriginal()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var corrupt = "not json at all";
        File.WriteAllText(store.FilePath, corrupt);
        store.Save(HotkeyConfig.CreateDefault());
        var backups = Directory.EnumerateFiles(td.Path, "hotkeys.preoverwrite.*.bad").ToList();
        bool backed = backups.Count == 1 && File.ReadAllText(backups[0]) == corrupt;
        bool nowValid = store.Load(out var err).ContentEquals(HotkeyConfig.CreateDefault()) && err == null;
        return (nameof(T_Hk_SaveBacksUpInvalidOriginal), backed && nowValid, $"backups={backups.Count} valid={nowValid}");
    }

    /// <summary>Real OS file lock (FileShare.None) makes Save fail; previous in-memory config,
    /// previous registrations and the on-disk bytes must all be unchanged.</summary>
    private static (string, bool, string?) T_Hk_LockedFileSave_PreservesMemoryAndRegistrations()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var initial = HotkeyConfig.CreateDefault();
        initial.Get(HotkeyActionId.Slot1).Enabled = true;
        initial.Get(HotkeyActionId.Slot1).ProfileId = "p1";
        store.Save(initial);
        var bytesBefore = File.ReadAllBytes(store.FilePath);
        var reg = new FakeRegistrar();
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        var registeredBefore = reg.Registered.ToDictionary(kv => kv.Key, kv => kv.Value);

        var proposed = ctl.Current;
        proposed.Get(HotkeyActionId.Slot1).Enabled = false;
        proposed.Get(HotkeyActionId.Slot2).Enabled = true;
        HotkeyCommitResult res;
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            res = ctl.Commit(proposed);
        }
        var bytesAfter = File.ReadAllBytes(store.FilePath);
        bool ok = res.Outcome == HotkeyCommitOutcome.SaveFailed
                  && ctl.Current.ContentEquals(initial)
                  && reg.Registered.Count == registeredBefore.Count
                  && reg.Registered.All(kv => registeredBefore.TryGetValue(kv.Key, out var v) && v == kv.Value)
                  && ctl.IsRegistered(HotkeyActionId.Slot1) && !ctl.IsRegistered(HotkeyActionId.Slot2)
                  && ctl.Statuses[HotkeyActionId.Slot1].State == HotkeyRegState.Registered
                  && bytesBefore.AsSpan().SequenceEqual(bytesAfter)
                  && !File.Exists(store.FilePath + ".tmp");
        return (nameof(T_Hk_LockedFileSave_PreservesMemoryAndRegistrations), ok,
            $"outcome={res.Outcome} regCount={reg.Registered.Count} slot1Reg={ctl.IsRegistered(HotkeyActionId.Slot1)}");
    }

    private static (string, bool, string?) T_Hk_InvalidCommit_ChangesNothing()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var reg = new FakeRegistrar();
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        var p = ctl.Current;
        p.Get(HotkeyActionId.Slot1).Enabled = true;
        p.Get(HotkeyActionId.Slot2).Enabled = true;
        p.Get(HotkeyActionId.Slot2).Key = Keys.D1; // duplicate of slot1
        var res = ctl.Commit(p);
        bool ok = res.Outcome == HotkeyCommitOutcome.Invalid && res.Errors.Count > 0
                  && !File.Exists(store.FilePath) && reg.RegisterCalls == 0
                  && ctl.Current.ContentEquals(HotkeyConfig.CreateDefault());
        return (nameof(T_Hk_InvalidCommit_ChangesNothing), ok, $"outcome={res.Outcome} file={File.Exists(store.FilePath)}");
    }

    private static (string, bool, string?) T_Hk_CollisionReportedTruthfully()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var reg = new FakeRegistrar();
        reg.ExternallyTaken.Add(Combo(true, true, false, Keys.D2));
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        var p = ctl.Current;
        p.Get(HotkeyActionId.Slot2).Enabled = true;
        var res = ctl.Commit(p);
        var st = res.Statuses[HotkeyActionId.Slot2];
        bool ok = res.Outcome == HotkeyCommitOutcome.Applied
                  && st.State == HotkeyRegState.Failed && st.Win32Error == HotkeyStatus.ERROR_HOTKEY_ALREADY_REGISTERED
                  && !ctl.IsRegistered(HotkeyActionId.Slot2)
                  && store.Load(out _).Get(HotkeyActionId.Slot2).Enabled; // user's choice persisted, status truthful
        return (nameof(T_Hk_CollisionReportedTruthfully), ok, $"state={st.State} err={st.Win32Error}");
    }

    private static (string, bool, string?) T_Hk_SwapCombos_NoSelfCollision()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var initial = HotkeyConfig.CreateDefault();
        initial.Get(HotkeyActionId.Slot1).Enabled = true;
        initial.Get(HotkeyActionId.Slot2).Enabled = true;
        store.Save(initial);
        var reg = new FakeRegistrar();
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        var p = ctl.Current;
        p.Get(HotkeyActionId.Slot1).Key = Keys.D2;
        p.Get(HotkeyActionId.Slot2).Key = Keys.D1;
        var res = ctl.Commit(p);
        bool ok = res.Outcome == HotkeyCommitOutcome.Applied
                  && ctl.IsRegistered(HotkeyActionId.Slot1) && ctl.IsRegistered(HotkeyActionId.Slot2)
                  && reg.Registered[HotkeyActions.RegistrationId(HotkeyActionId.Slot1)] == Combo(true, true, false, Keys.D2)
                  && reg.Registered[HotkeyActions.RegistrationId(HotkeyActionId.Slot2)] == Combo(true, true, false, Keys.D1);
        return (nameof(T_Hk_SwapCombos_NoSelfCollision), ok, $"outcome={res.Outcome}");
    }

    private static (string, bool, string?) T_Hk_ProfileOnlyChange_DoesNotReRegister()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var initial = HotkeyConfig.CreateDefault();
        initial.Get(HotkeyActionId.Slot1).Enabled = true;
        store.Save(initial);
        var reg = new FakeRegistrar();
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        int callsBefore = reg.RegisterCalls, unregBefore = reg.UnregisterCalls;
        var p = ctl.Current;
        p.Get(HotkeyActionId.Slot1).ProfileId = "another";
        var res = ctl.Commit(p);
        bool ok = res.Outcome == HotkeyCommitOutcome.Applied && reg.RegisterCalls == callsBefore
                  && reg.UnregisterCalls == unregBefore && ctl.IsRegistered(HotkeyActionId.Slot1)
                  && store.Load(out _).Get(HotkeyActionId.Slot1).ProfileId == "another";
        return (nameof(T_Hk_ProfileOnlyChange_DoesNotReRegister), ok, $"reg={reg.RegisterCalls - callsBefore} unreg={reg.UnregisterCalls - unregBefore}");
    }

    private static (string, bool, string?) T_Hk_Shutdown_UnregistersAll()
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        var initial = HotkeyConfig.CreateDefault();
        foreach (var b in initial.Bindings) b.Enabled = true;
        store.Save(initial);
        var reg = new FakeRegistrar();
        var ctl = new HotkeyController(store, reg);
        ctl.Initialize();
        int before = reg.Registered.Count;
        ctl.Shutdown();
        bool ok = before == 10 && reg.Registered.Count == 0 && HotkeyActions.All.All(a => !ctl.IsRegistered(a));
        return (nameof(T_Hk_Shutdown_UnregistersAll), ok, $"before={before} after={reg.Registered.Count}");
    }

    private static (string, bool, string?) T_Hk_ConfiguringDoesNotMoveWindows()
    {
        using var td = new TempDir();
        var profile = MakeProfile(("np", @"C:\x\np.exe", "N", null, 500, 500, 400, 300));
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\np.exe", "N", "Untitled"));
        var profiles = new ProfileStore(td.Path); profiles.Save(profile);
        var mon = new MonitoringService(fake, profiles, new SettingsStore(td.Path));
        mon.SetActiveProfile(profile);
        mon.Resume();
        var ctl = new HotkeyController(new HotkeyStore(td.Path), new FakeRegistrar());
        ctl.Initialize();
        var p = ctl.Current;
        p.Get(HotkeyActionId.Slot1).ProfileId = profile.Id;
        p.Get(HotkeyActionId.Slot1).Enabled = true;
        p.Get(HotkeyActionId.Next).Enabled = true;
        var res = ctl.Commit(p);
        for (int i = 0; i < 3; i++) mon.Tick();
        bool ok = res.Outcome == HotkeyCommitOutcome.Applied && fake.Moves.Count == 0;
        return (nameof(T_Hk_ConfiguringDoesNotMoveWindows), ok, $"moves={fake.Moves.Count}");
    }

    // ---------- Round 22: duplicate JSON properties, own-thread title deadlock, capture ----------

    private static string ReplaceFirst(string s, string find, string repl)
    {
        int i = s.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + repl + s[(i + find.Length)..];
    }

    /// <summary>Shared assertion: Load returns all-disabled defaults with an error, never throws,
    /// leaves the original bytes untouched and keeps one identical .bad copy.</summary>
    private static (bool ok, string detail) DuplicateCase(string json)
    {
        using var td = new TempDir();
        var store = new HotkeyStore(td.Path);
        File.WriteAllText(store.FilePath, json);
        var before = File.ReadAllBytes(store.FilePath);
        HotkeyConfig cfg;
        string? err;
        try { cfg = store.Load(out err); }
        catch (Exception ex) { return (false, "load_threw:" + ex.GetType().Name); }
        bool parseIsInvalidData;
        try { HotkeyStore.ParseAndValidate(json); parseIsInvalidData = false; }
        catch (InvalidDataException) { parseIsInvalidData = true; }
        catch { parseIsInvalidData = false; }
        bool intact = before.AsSpan().SequenceEqual(File.ReadAllBytes(store.FilePath));
        var copies = Directory.EnumerateFiles(td.Path, "hotkeys.corrupt.*.bad").ToList();
        bool preserved = copies.Count == 1 && File.ReadAllBytes(copies[0]).AsSpan().SequenceEqual(before);
        bool ok = err != null && cfg.ContentEquals(HotkeyConfig.CreateDefault()) && parseIsInvalidData && intact && preserved;
        return (ok, $"err={err} parseInvalidData={parseIsInvalidData} intact={intact} preserved={preserved}");
    }

    private static (string, bool, string?) T_Hk_DuplicateRootProperty_MalformedNotCrash()
    {
        var baseline = HotkeyStore.Serialize(HotkeyConfig.CreateDefault());
        var dup = ReplaceFirst(baseline, "\"version\": 1,", "\"version\": 1, \"version\": 1,");
        if (dup == baseline) return (nameof(T_Hk_DuplicateRootProperty_MalformedNotCrash), false, "fixture_not_built");
        var (ok, detail) = DuplicateCase(dup);
        return (nameof(T_Hk_DuplicateRootProperty_MalformedNotCrash), ok, detail);
    }

    private static (string, bool, string?) T_Hk_DuplicateBindingProperty_MalformedNotCrash()
    {
        var baseline = HotkeyStore.Serialize(HotkeyConfig.CreateDefault());
        var dup = ReplaceFirst(baseline, "\"enabled\": false,", "\"enabled\": false, \"enabled\": true,");
        if (dup == baseline) return (nameof(T_Hk_DuplicateBindingProperty_MalformedNotCrash), false, "fixture_not_built");
        var (ok, detail) = DuplicateCase(dup);
        return (nameof(T_Hk_DuplicateBindingProperty_MalformedNotCrash), ok, detail);
    }

    private static (string, bool, string?) T_Settings_DuplicateProperty_DefaultsNotCrash()
    {
        using var td = new TempDir();
        var path = Path.Combine(td.Path, "settings.json");
        const string raw = "{\"version\":1,\"version\":1,\"activeProfileId\":null,\"monitoringEnabled\":true}";
        File.WriteAllText(path, raw);
        var ss = new SettingsStore(td.Path);
        AppSettings s;
        try { s = ss.Load(); }
        catch (Exception ex) { return (nameof(T_Settings_DuplicateProperty_DefaultsNotCrash), false, "load_threw:" + ex.GetType().Name); }
        bool ok = !string.IsNullOrEmpty(ss.LastLoadError) && !s.MonitoringEnabled && s.ActiveProfileId == null
                  && File.ReadAllText(path) == raw;
        return (nameof(T_Settings_DuplicateProperty_DefaultsNotCrash), ok, $"err='{ss.LastLoadError}'");
    }

    /// <summary>
    /// Hang regression (v0.1.0 "응답 없음"): a window owned by ANOTHER THREAD OF THIS PROCESS whose
    /// thread is not pumping messages (here: waiting on an event, like SystemEvents waiting on the
    /// UI thread). Enumerating from this thread must not send it a synchronous WM_GETTEXT. The
    /// fixture thread auto-releases after 4 s, so a regression shows up as a ~4 s enumerate, never
    /// a permanent deadlock. No foreign window is touched; only own-process windows are created.
    /// </summary>
    private static (string, bool, string?) T_Enumerate_OwnBlockedThreadWindow_NoTitleWait()
    {
        using var ready = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        IntPtr hwnd = IntPtr.Zero;
        var th = new Thread(() =>
        {
            using var f = new Form { Text = "wlm-own-thread-hang-probe", ShowInTaskbar = false };
            hwnd = f.Handle; // creates the (hidden) window on this thread
            ready.Set();
            release.Wait(4000); // NOT pumping: a sent WM_GETTEXT would block until this returns
        })
        { IsBackground = true, Name = "wlm-hang-probe" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!ready.Wait(3000))
        {
            release.Set();
            return (nameof(T_Enumerate_OwnBlockedThreadWindow_NoTitleWait), false, "fixture_not_ready");
        }

        var en = new WindowEnumerator();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = en.Enumerate(Environment.ProcessId);
        long t1 = sw.ElapsedMilliseconds;
        var meta = en.EnumerateAnyTopLevelWithMetadata(Environment.ProcessId);
        long t2 = sw.ElapsedMilliseconds;
        // A caller passing some OTHER pid must still never read our own windows' titles.
        var resOther = en.Enumerate(0);
        long t3 = sw.ElapsedMilliseconds;
        sw.Stop();
        release.Set();
        bool joined = th.Join(6000);

        var ex = res.Excluded.FirstOrDefault(e => e.Hwnd == hwnd);
        var exOther = resOther.Excluded.FirstOrDefault(e => e.Hwnd == hwnd);
        bool ownIncluded = res.Windows.Any(w => w.Identity.Pid == Environment.ProcessId)
                           || resOther.Windows.Any(w => w.Identity.Pid == Environment.ProcessId)
                           || meta.Any(w => w.Identity.Pid == Environment.ProcessId);
        long maxCallMs = Math.Max(t1, Math.Max(t2 - t1, t3 - t2));
        bool ok = maxCallMs < 1000 && joined
                  && ex != null && ex.Reason == "own_process" && ex.Title == ""
                  && exOther != null && exOther.Reason == "own_process" && exOther.Title == ""
                  && !ownIncluded;
        return (nameof(T_Enumerate_OwnBlockedThreadWindow_NoTitleWait), ok,
            $"enumMs={t1} metaMs={t2 - t1} otherPidMs={t3 - t2} totalMs={t3} joined={joined} reason={ex?.Reason} ownIncluded={ownIncluded}");
    }

    private static (string, bool, string?) T_CaptureBox_CaptureComboValidates()
    {
        using var box = new App.HotkeyCaptureBox();
        box.SetCombo(true, true, false, Keys.D1);
        string? lastFeedback = "none";
        box.Feedback += m => lastFeedback = m;
        bool f12 = box.CaptureCombo(true, true, false, Keys.F12);
        bool f12KeptOld = box.Key == Keys.D1 && lastFeedback != null;
        bool noMod = box.CaptureCombo(false, false, true, Keys.A);
        bool ok23 = box.CaptureCombo(true, true, true, Keys.F23);
        bool ok = !f12 && f12KeptOld && !noMod && ok23 && box.Key == Keys.F23 && box.Shift && lastFeedback == null
                  && box.Text == "Ctrl+Alt+Shift+F23";
        return (nameof(T_CaptureBox_CaptureComboValidates), ok, $"text={box.Text}");
    }

    // ---------- Round 24: icon, title, second-launch activation (isolated QA event names) ----------

    private static (string, bool, string?) T_AppIcon_EmbeddedLoadsAllSizes()
    {
        var sizes = App.AppIcon.FrameSizes();
        var required = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
        bool framesOk = required.All(sizes.Contains);
        bool small = false, large = false;
        if (App.AppIcon.Icon is { } ico)
        {
            using var s16 = new Icon(ico, 16, 16);
            using var s32 = new Icon(ico, 32, 32);
            small = s16.Width == 16 && s16.Height == 16;
            large = s32.Width == 32 && s32.Height == 32;
        }
        bool ok = App.AppIcon.IsLoaded && App.AppIcon.LoadError == null && framesOk && small && large;
        return (nameof(T_AppIcon_EmbeddedLoadsAllSizes), ok,
            $"loaded={App.AppIcon.IsLoaded} err={App.AppIcon.LoadError} sizes=[{string.Join(",", sizes)}] s16={small} s32={large}");
    }

    private static (string, bool, string?) T_AppInfo_TitleHasAssemblyVersion()
    {
        var v = typeof(App.AppInfo).Assembly.GetName().Version!;
        var expected = $"{v.Major}.{v.Minor}.{v.Build}";
        var title = App.AppInfo.ProductTitle;
        bool ok = App.AppInfo.DisplayVersion == expected && title.Contains("WindowDock") && title.EndsWith("v" + expected);
        return (nameof(T_AppInfo_TitleHasAssemblyVersion), ok, title);
    }

    private static (string, bool, string?) T_Activation_NormalSecondLaunchSignalsListenerOnce()
    {
        var name = App.SingleInstanceActivation.EventNameForScope("st-" + Guid.NewGuid().ToString("N")[..12]);
        using var fired = new ManualResetEventSlim(false);
        int calls = 0;
        using var listener = new App.ActivationListener(name, () => { Interlocked.Increment(ref calls); fired.Set(); });
        var outcome = App.SingleInstanceActivation.HandleSecondInstance(false, name, TimeSpan.FromSeconds(2));
        bool got = fired.Wait(2000);
        Thread.Sleep(200); // a spurious second callback would show up here
        bool ok = outcome == App.SecondInstanceOutcome.ActivatedExisting && got && Volatile.Read(ref calls) == 1 && listener.SignalCount == 1;
        return (nameof(T_Activation_NormalSecondLaunchSignalsListenerOnce), ok, $"outcome={outcome} calls={calls}");
    }

    private static (string, bool, string?) T_Activation_DuplicateAutostartIsSilentAndSendsNothing()
    {
        var name = App.SingleInstanceActivation.EventNameForScope("st-" + Guid.NewGuid().ToString("N")[..12]);
        int calls = 0;
        using var listener = new App.ActivationListener(name, () => Interlocked.Increment(ref calls));
        var outcome = App.SingleInstanceActivation.HandleSecondInstance(true, name, TimeSpan.FromSeconds(2));
        Thread.Sleep(500);
        bool ok = outcome == App.SecondInstanceOutcome.SilentAutostartDuplicate && Volatile.Read(ref calls) == 0 && listener.SignalCount == 0;
        return (nameof(T_Activation_DuplicateAutostartIsSilentAndSendsNothing), ok, $"outcome={outcome} calls={calls}");
    }

    private static (string, bool, string?) T_Activation_NoListenerReportsUnavailableQuickly()
    {
        var name = App.SingleInstanceActivation.EventNameForScope("st-none-" + Guid.NewGuid().ToString("N")[..12]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = App.SingleInstanceActivation.HandleSecondInstance(false, name, TimeSpan.FromMilliseconds(300));
        sw.Stop();
        // After a listener is disposed its name must be gone again (no leaked handle).
        var name2 = App.SingleInstanceActivation.EventNameForScope("st-gone-" + Guid.NewGuid().ToString("N")[..12]);
        var l = new App.ActivationListener(name2, () => { });
        l.Dispose();
        bool released = !EventWaitHandle.TryOpenExisting(name2, out var leftover);
        leftover?.Dispose();
        bool ok = outcome == App.SecondInstanceOutcome.ActivationUnavailable && sw.ElapsedMilliseconds < 2000 && released;
        return (nameof(T_Activation_NoListenerReportsUnavailableQuickly), ok, $"outcome={outcome} ms={sw.ElapsedMilliseconds} released={released}");
    }

    // ---------- Previous / Next ordering ----------

    private static (string, bool, string?) T_Nav_EmptyListReturnsNull()
    {
        bool ok = ProfileOrdering.ResolveNeighbor(Array.Empty<string>(), "a", "b", true) is null
                  && ProfileOrdering.ResolveNeighbor(Array.Empty<string>(), null, null, false) is null;
        return (nameof(T_Nav_EmptyListReturnsNull), ok, null);
    }

    private static (string, bool, string?) T_Nav_NoAnchor_NextFirstPreviousLast()
    {
        var ids = new[] { "a", "b", "c" };
        bool ok = ProfileOrdering.ResolveNeighbor(ids, null, null, true) == "a"
                  && ProfileOrdering.ResolveNeighbor(ids, null, null, false) == "c"
                  && ProfileOrdering.ResolveNeighbor(ids, "gone", "also-gone", true) == "a";
        return (nameof(T_Nav_NoAnchor_NextFirstPreviousLast), ok, null);
    }

    private static (string, bool, string?) T_Nav_ActiveAnchor_WrapsBothDirections()
    {
        var ids = new[] { "a", "b", "c" };
        bool ok = ProfileOrdering.ResolveNeighbor(ids, "b", "a", true) == "c"
                  && ProfileOrdering.ResolveNeighbor(ids, "c", null, true) == "a"
                  && ProfileOrdering.ResolveNeighbor(ids, "a", null, false) == "c"
                  && ProfileOrdering.ResolveNeighbor(ids, "b", "c", false) == "a"
                  && ProfileOrdering.ResolveNeighbor(new[] { "only" }, "only", null, true) == "only";
        return (nameof(T_Nav_ActiveAnchor_WrapsBothDirections), ok, null);
    }

    private static (string, bool, string?) T_Nav_FallsBackToSelectedWhenActiveMissing()
    {
        var ids = new[] { "a", "b", "c" };
        bool ok = ProfileOrdering.ResolveNeighbor(ids, "deleted", "b", true) == "c"
                  && ProfileOrdering.ResolveNeighbor(ids, null, "b", false) == "a";
        return (nameof(T_Nav_FallsBackToSelectedWhenActiveMissing), ok, null);
    }

    private static (string, bool, string?) T_Nav_SameNameTies_StableById()
    {
        var p1 = NamedProfile("zz9", "Same");
        var p2 = NamedProfile("aa1", "Same");
        var p3 = NamedProfile("mm5", "Same");
        var p0 = NamedProfile("x00", "Alpha");
        var order1 = ProfileOrdering.Sort(new[] { p1, p2, p3, p0 }).Select(p => p.Id).ToList();
        var order2 = ProfileOrdering.Sort(new[] { p3, p0, p1, p2 }).Select(p => p.Id).ToList();
        bool ok = order1.SequenceEqual(order2) && order1.SequenceEqual(new[] { "x00", "aa1", "mm5", "zz9" })
                  && ProfileOrdering.ResolveNeighbor(order1, "aa1", null, true) == "mm5";
        return (nameof(T_Nav_SameNameTies_StableById), ok, string.Join(",", order1));
    }

    private static (string, bool, string?) T_Nav_IncludesProfilesBeyondEightSlots()
    {
        var ids = Enumerable.Range(1, 10).Select(i => $"p{i:00}").ToList();
        bool ok = ProfileOrdering.ResolveNeighbor(ids, "p08", null, true) == "p09"
                  && ProfileOrdering.ResolveNeighbor(ids, "p09", null, true) == "p10"
                  && ProfileOrdering.ResolveNeighbor(ids, "p10", null, true) == "p01"
                  && ProfileOrdering.ResolveNeighbor(ids, "p01", null, false) == "p10";
        return (nameof(T_Nav_IncludesProfilesBeyondEightSlots), ok, null);
    }

    // ---------- quick apply service (FakeProvider, no native windows) ----------

    private static (FakeProvider fake, ProfileStore store, MonitoringService mon, Profile profile) QuickFixture(TempDir td, int x = 500, int y = 500)
    {
        var profile = MakeProfile(("np", @"C:\x\np.exe", "N", null, x, y, 400, 300));
        profile.Id = "quick01";
        var fake = new FakeProvider { Monitors = { Mon(0, 0, 1920, 1080) } };
        fake.AddWindow(Win(1, 100, @"C:\x\np.exe", "N", "Untitled", x: 10, y: 10));
        var store = new ProfileStore(td.Path);
        store.Save(profile);
        var mon = new MonitoringService(fake, store, new SettingsStore(td.Path));
        mon.InitializeFromSettings();
        return (fake, store, mon, profile);
    }

    private static (string, bool, string?) T_Quick_Unassigned_DoesNothing()
    {
        using var td = new TempDir();
        var (fake, store, mon, _) = QuickFixture(td);
        var r = QuickApply.ApplyById(store, mon, null);
        bool ok = r.Outcome == QuickApplyOutcome.Unassigned && fake.Moves.Count == 0 && mon.ActiveProfile is null && !mon.IsMonitoring;
        return (nameof(T_Quick_Unassigned_DoesNothing), ok, $"outcome={r.Outcome} moves={fake.Moves.Count}");
    }

    private static (string, bool, string?) T_Quick_DeletedId_NeverAppliesOther()
    {
        using var td = new TempDir();
        var (fake, store, mon, _) = QuickFixture(td);
        var r = QuickApply.ApplyById(store, mon, "deleted01");
        bool ok = r.Outcome == QuickApplyOutcome.ProfileMissing && fake.Moves.Count == 0 && mon.ActiveProfile is null && !mon.IsMonitoring;
        return (nameof(T_Quick_DeletedId_NeverAppliesOther), ok, $"outcome={r.Outcome} moves={fake.Moves.Count}");
    }

    private static (string, bool, string?) T_Quick_CorruptFile_NeverAppliesOther()
    {
        using var td = new TempDir();
        var (fake, store, mon, _) = QuickFixture(td);
        File.WriteAllText(Path.Combine(store.ProfilesDir, "bad01.json"), "{ corrupt");
        var r = QuickApply.ApplyById(store, mon, "bad01");
        bool ok = r.Outcome == QuickApplyOutcome.ProfileUnreadable && fake.Moves.Count == 0 && mon.ActiveProfile is null
                  && File.ReadAllText(Path.Combine(store.ProfilesDir, "bad01.json")) == "{ corrupt";
        return (nameof(T_Quick_CorruptFile_NeverAppliesOther), ok, $"outcome={r.Outcome} moves={fake.Moves.Count}");
    }

    private static (string, bool, string?) T_Quick_UnsafeId_Rejected()
    {
        using var td = new TempDir();
        var (fake, store, mon, _) = QuickFixture(td);
        var r = QuickApply.ApplyById(store, mon, "../quick01");
        bool ok = r.Outcome == QuickApplyOutcome.ProfileUnreadable && fake.Moves.Count == 0 && mon.ActiveProfile is null;
        return (nameof(T_Quick_UnsafeId_Rejected), ok, $"outcome={r.Outcome}");
    }

    private static (string, bool, string?) T_Quick_LoadsFreshFromDisk()
    {
        using var td = new TempDir();
        var (fake, store, mon, profile) = QuickFixture(td, x: 100, y: 100);
        // The disk copy changes after any cache could have been taken; quick apply must use disk.
        var onDisk = store.Load(profile.Id)!;
        onDisk.Entries[0].Rect = new SavedRect { X = 700, Y = 600, Width = 400, Height = 300 };
        store.Save(onDisk);
        var r = QuickApply.ApplyById(store, mon, profile.Id);
        bool ok = r.Outcome == QuickApplyOutcome.Applied && fake.Moves.Count == 1
                  && fake.Moves[0].target.X == 700 && fake.Moves[0].target.Y == 600;
        return (nameof(T_Quick_LoadsFreshFromDisk), ok,
            fake.Moves.Count > 0 ? $"target={fake.Moves[0].target.X},{fake.Moves[0].target.Y}" : "no_move");
    }

    private static (string, bool, string?) T_Quick_Applied_SetsActiveEnablesAndPersistsMonitoring()
    {
        using var td = new TempDir();
        var (fake, store, mon, profile) = QuickFixture(td);
        var r = QuickApply.ApplyById(store, mon, profile.Id);
        var persisted = new SettingsStore(td.Path).Load();
        bool ok = r.Outcome == QuickApplyOutcome.Applied && r.ProfileId == profile.Id
                  && mon.ActiveProfile?.Id == profile.Id && mon.IsMonitoring
                  && persisted.ActiveProfileId == profile.Id && persisted.MonitoringEnabled
                  && r.Reports.Count == 1 && r.Reports[0].Result == PlaceResult.Moved;
        return (nameof(T_Quick_Applied_SetsActiveEnablesAndPersistsMonitoring), ok,
            $"active={mon.ActiveProfile?.Id} mon={mon.IsMonitoring} persisted={persisted.ActiveProfileId}/{persisted.MonitoringEnabled}");
    }
}
