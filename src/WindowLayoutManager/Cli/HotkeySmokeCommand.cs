using System.Diagnostics;
using WindowLayoutManager.App;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

/// <summary>
/// --hotkey-smoke --output &lt;r&gt; [--wait-os-hotkey &lt;seconds&gt;]
///
/// Isolated v0.2.0 slot/hotkey lifecycle smoke. Uses a fresh temp data directory and an IN-MEMORY
/// mock window provider only — no real user window is enumerated, moved or closed, and no default
/// user profile/settings/hotkeys file is read or written. It does register REAL global hotkeys
/// (RegisterHotKey) for obscure combinations (Ctrl+Alt+Shift+F20..F23) for the duration of the run
/// and releases them on exit.
///
/// Every step is labeled:
///   synthetic_wm_hotkey — WM_HOTKEY posted by this process to its own message window. Proves our
///                         message handling + apply path, NOT the OS keyboard routing.
///   button_click        — real Button.PerformClick on the visible main form.
///   os_keyboard_routing — only with --wait-os-hotkey: waits for an EXTERNAL key press (e.g. the
///                         verifier's native SendInput of Ctrl+Alt+Shift+F23). A ready marker
///                         &lt;output&gt;.ready.json is written when the harness starts waiting.
/// </summary>
public static class HotkeySmokeCommand
{
    private static readonly Dictionary<string, WindowRect> ProfileRects = new()
    {
        ["hk-a"] = new WindowRect(100, 100, 400, 300),
        ["hk-b"] = new WindowRect(200, 150, 420, 300),
        ["hk-b2"] = new WindowRect(300, 200, 440, 300),
        ["hk-c"] = new WindowRect(400, 250, 460, 300)
    };

    public sealed class StepRecord
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool Completed { get; set; }
        public bool Dispatched { get; set; }
        public string? Outcome { get; set; }
        public string? ExpectedOutcome { get; set; }
        public string? ActiveId { get; set; }
        public string? ExpectedActiveId { get; set; }
        public bool MonitoringOn { get; set; }
        public int MovesDelta { get; set; }
        public int ExpectedMovesDelta { get; set; }
        public string? LastMoveTarget { get; set; }
        public bool LastMoveRectOk { get; set; }
        public string? Note { get; set; }
        public bool Pass { get; set; }
    }

    public sealed class Result
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string DataDirName { get; set; } = "";
        public List<string> Timeouts { get; set; } = new();
        public string? HotkeyLoadError { get; set; }

        // Startup (tray-only, before any explicit command)
        public bool StartupHidden { get; set; }
        public int StartupMoves { get; set; }
        public string? StartupActiveId { get; set; }
        public bool StartupMonitoring { get; set; }
        public Dictionary<string, string> StartupStatuses { get; set; } = new();
        public bool StartupRegistrationsOk { get; set; }
        public List<string> StartupSlotTexts { get; set; } = new();
        public bool StartupSlotTextsOk { get; set; }

        public List<StepRecord> Steps { get; set; } = new();

        public List<string> ProfileListIds { get; set; } = new();
        public bool ProfileListOrderOk { get; set; }

        public string? CommitEnableOutcome { get; set; }
        public string? CommitEnableSlot5Status { get; set; }
        public int CommitEnableMovesDelta { get; set; }
        public bool CommitEnableOk { get; set; }

        public string? LockedCommitOutcome { get; set; }
        public bool LockedCommitConfigUnchanged { get; set; }
        public bool LockedCommitStatusUnchanged { get; set; }
        public bool LockedCommitFileUnchanged { get; set; }
        public bool LockedCommitOk { get; set; }

        public bool DialogDefaultLayoutOk { get; set; }
        public bool DialogMinLayoutOk { get; set; }
        public string? DialogDetail { get; set; }
        public bool MainMinLayoutOk { get; set; }
        public string? MainMinDetail { get; set; }

        public string OsDispatch { get; set; } = "not_requested";
        public bool ApplicationRunReturned { get; set; }
        public Dictionary<string, string> ReleaseProbe { get; set; } = new();
        public bool ReleasedAfterExit { get; set; }
    }

    public static int Run(string[] args)
    {
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }
        int waitOsSeconds = int.TryParse(CliCommon.GetArg(args, "--wait-os-hotkey"), out var w) ? Math.Clamp(w, 0, 300) : 0;

        var r = new Result();
        var dataDir = Path.Combine(Path.GetTempPath(), "wlm-hotkey-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        r.DataDirName = Path.GetFileName(dataDir);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        var outBase = Path.GetFileNameWithoutExtension(outPath);

        // ---------- isolated fixture data ----------
        var profiles = new ProfileStore(dataDir);
        profiles.Save(MakeProfile("hk-a", "Alpha"));
        profiles.Save(MakeProfile("hk-b", "Beta"));
        profiles.Save(MakeProfile("hk-b2", "Beta")); // same-name tie → ordered by id after hk-b
        profiles.Save(MakeProfile("hk-c", "Gamma"));
        File.WriteAllText(Path.Combine(profiles.ProfilesDir, "hk-d.json"), "{ corrupt profile for hotkey smoke");
        var settings = new SettingsStore(dataDir);
        settings.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = false });

        var hotkeyStore = new HotkeyStore(dataDir);
        var cfg = HotkeyConfig.CreateDefault();
        SetBinding(cfg, HotkeyActionId.Slot1, "hk-b", true, Keys.F23);
        cfg.Get(HotkeyActionId.Slot3).ProfileId = "hkdeleted";   // never existed → missing
        cfg.Get(HotkeyActionId.Slot4).ProfileId = "hk-d";        // corrupt file → unreadable
        SetBinding(cfg, HotkeyActionId.Previous, null, true, Keys.F21);
        SetBinding(cfg, HotkeyActionId.Next, null, true, Keys.F22);
        hotkeyStore.Save(cfg);

        var mock = new MockProvider();
        var windowId = new WindowIdentity(new IntPtr(1), 4101, 1000);
        mock.AddWindow(new WindowInfo
        {
            Identity = windowId,
            ExePath = @"C:\x\hk.exe",
            ProcessName = "hk",
            ClassName = "HkClass",
            Title = "hk-window main",
            CurrentRect = new WindowRect(10, 10, 300, 200),
            NormalRect = new WindowRect(10, 10, 300, 200),
            ShowState = WindowShowState.Normal,
            MonitorDevice = @"\\.\DISPLAY1"
        });

        var monitor = new MonitoringService(mock, profiles, settings);
        monitor.InitializeFromSettings();
        var form = new MainForm(profiles, settings, monitor, mock, startInTray: true, hotkeyStore: hotkeyStore);
        form.UiSmokeSuppressDialogs(true);

        // ---------- steps ----------
        var steps = new List<Step>();
        steps.Add(new Step("startup_state", 500, () => { }, () => true, _ =>
        {
            r.HotkeyLoadError = form.UiSmokeHotkeyLoadError;
            r.StartupHidden = !form.Visible;
            r.StartupMoves = mock.Moves.Count;
            r.StartupActiveId = monitor.ActiveProfile?.Id;
            r.StartupMonitoring = monitor.IsMonitoring;
            var st = form.UiSmokeHotkeyStatuses ?? new Dictionary<HotkeyActionId, HotkeyStatus>();
            foreach (var kv in st) r.StartupStatuses[HotkeyActions.JsonName(kv.Key)] = kv.Value.State + (kv.Value.Win32Error != 0 ? $":{kv.Value.Win32Error}" : "");
            r.StartupRegistrationsOk =
                st.TryGetValue(HotkeyActionId.Slot1, out var s1) && s1.State == HotkeyRegState.Registered &&
                st.TryGetValue(HotkeyActionId.Previous, out var sp) && sp.State == HotkeyRegState.Registered &&
                st.TryGetValue(HotkeyActionId.Next, out var sn) && sn.State == HotkeyRegState.Registered &&
                HotkeyActions.All.Where(a => a is not HotkeyActionId.Slot1 and not HotkeyActionId.Previous and not HotkeyActionId.Next)
                    .All(a => st.TryGetValue(a, out var sx) && sx.State == HotkeyRegState.Disabled);
            for (int i = 1; i <= 8; i++) r.StartupSlotTexts.Add(form.UiSmokeSlotButtonText(i));
            r.StartupSlotTextsOk =
                r.StartupSlotTexts[0].StartsWith("1") && r.StartupSlotTexts[0].Contains("Beta") &&
                r.StartupSlotTexts[1].StartsWith("2") && r.StartupSlotTexts[1].Contains("비어") &&
                r.StartupSlotTexts[2].StartsWith("3") && r.StartupSlotTexts[2].Contains("찾을 수 없음") &&
                r.StartupSlotTexts[3].StartsWith("4") && r.StartupSlotTexts[3].Contains("찾을 수 없음");
        }));

        Step Quick(string name, string kind, Func<bool> trigger, QuickApplyOutcome expectOutcome, string? expectActive, int expectMoves, string? expectRectId)
        {
            var rec = new StepRecord
            {
                Name = name, Kind = kind, ExpectedOutcome = expectOutcome.ToString(),
                ExpectedActiveId = expectActive, ExpectedMovesDelta = expectMoves
            };
            int countBefore = 0, movesBefore = 0;
            return new Step(name, 4000,
                () =>
                {
                    countBefore = form.UiSmokeQuickApplyCount;
                    movesBefore = mock.Moves.Count;
                    rec.Dispatched = trigger();
                },
                () => form.UiSmokeQuickApplyCount > countBefore,
                done =>
                {
                    var res = form.LastQuickApplyResult;
                    rec.Completed = done;
                    rec.Outcome = done ? res?.Outcome.ToString() : null;
                    rec.ActiveId = monitor.ActiveProfile?.Id;
                    rec.MonitoringOn = monitor.IsMonitoring;
                    rec.MovesDelta = mock.Moves.Count - movesBefore;
                    if (rec.MovesDelta > 0)
                    {
                        var t = mock.Moves[^1].target;
                        rec.LastMoveTarget = $"{t.X},{t.Y} {t.Width}x{t.Height}";
                        rec.LastMoveRectOk = expectRectId != null && ProfileRects[expectRectId] == t;
                    }
                    rec.Pass = done && rec.Dispatched
                        && rec.Outcome == expectOutcome.ToString()
                        && rec.ActiveId == expectActive
                        && rec.MovesDelta == expectMoves
                        && (expectRectId == null || rec.LastMoveRectOk)
                        && (expectOutcome != QuickApplyOutcome.Applied || rec.MonitoringOn);
                    r.Steps.Add(rec);
                });
        }

        const string Syn = "synthetic_wm_hotkey";
        const string Btn = "button_click";
        // Tray-only: synthetic WM_HOTKEY through the real message window + controller id check.
        steps.Add(Quick("synthetic_slot1", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Slot1), QuickApplyOutcome.Applied, "hk-b", 1, "hk-b"));
        steps.Add(Quick("synthetic_next_tie", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Next), QuickApplyOutcome.Applied, "hk-b2", 1, "hk-b2"));
        steps.Add(Quick("synthetic_next", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Next), QuickApplyOutcome.Applied, "hk-c", 1, "hk-c"));
        steps.Add(Quick("synthetic_next_wrap", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Next), QuickApplyOutcome.Applied, "hk-a", 1, "hk-a"));
        steps.Add(Quick("synthetic_previous_wrap", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Previous), QuickApplyOutcome.Applied, "hk-c", 1, "hk-c"));
        // A disabled slot's id is not registered → a synthetic WM_HOTKEY for it must be ignored.
        steps.Add(IgnoredSynthetic("synthetic_disabled_slot2_ignored", HotkeyActionId.Slot2));

        steps.Add(new Step("open_from_tray", 3000, () => form.TrayOpenItem.PerformClick(), () => form.Visible, done =>
        {
            if (!done) r.Timeouts.Add("open_from_tray");
        }));
        steps.Add(Quick("button_slot2_unassigned", Btn, () => { form.UiSmokeClickSlot(2); return true; }, QuickApplyOutcome.Unassigned, "hk-c", 0, null));
        steps.Add(Quick("button_slot3_deleted", Btn, () => { form.UiSmokeClickSlot(3); return true; }, QuickApplyOutcome.ProfileMissing, "hk-c", 0, null));
        steps.Add(Quick("button_slot4_corrupt", Btn, () => { form.UiSmokeClickSlot(4); return true; }, QuickApplyOutcome.ProfileUnreadable, "hk-c", 0, null));
        steps.Add(Quick("button_previous", Btn, () => { form.UiSmokeClickPrevious(); return true; }, QuickApplyOutcome.Applied, "hk-b2", 1, "hk-b2"));
        steps.Add(Quick("button_next", Btn, () => { form.UiSmokeClickNext(); return true; }, QuickApplyOutcome.Applied, "hk-c", 1, "hk-c"));

        // ---- Round 22: hotkeys must be dropped (not queued) while an OWNED modal is open ----
        void AddModalScenario(string label, Action openBlocking, Func<bool> isOpen, Action close)
        {
            var rec = new StepRecord { Name = $"hotkey_during_{label}", Kind = Syn, ExpectedOutcome = "suppressed", ExpectedMovesDelta = 0 };
            int qBefore = 0, invBefore = 0, movesBefore = 0, supBefore = 0;
            string? activeBefore = null, selectedBefore = null;
            steps.Add(new Step($"open_{label}", 3000,
                () =>
                {
                    qBefore = form.UiSmokeQuickApplyCount;
                    invBefore = form.UiSmokeHotkeyInvocationCount;
                    movesBefore = mock.Moves.Count;
                    supBefore = form.UiSmokeHotkeySuppressedCount;
                    activeBefore = monitor.ActiveProfile?.Id;
                    selectedBefore = form.UiSmokeSelectedProfile()?.Id;
                    form.BeginInvoke(openBlocking);
                },
                isOpen,
                done => { if (!done) r.Timeouts.Add($"open_{label}"); }));
            // Next would change the active profile if it were (wrongly) applied.
            steps.Add(new Step($"post_during_{label}", 1500,
                () => rec.Dispatched = form.UiSmokePostSyntheticHotkey(HotkeyActionId.Next),
                () => false, _ => { }, timeoutIsSuccess: true));
            steps.Add(new Step($"close_{label}", 3000, close,
                () => !form.UiSmokeModalOpen && form.UiSmokeOwnerEnabledNative,
                done => { if (!done) r.Timeouts.Add($"close_{label}"); }));
            // Wait after closing: a queued command must NOT run once the modal is gone.
            steps.Add(new Step($"after_{label}", 1200, () => { }, () => false, _ =>
            {
                rec.Completed = true;
                rec.ExpectedActiveId = activeBefore;
                rec.ActiveId = monitor.ActiveProfile?.Id;
                rec.MonitoringOn = monitor.IsMonitoring;
                rec.MovesDelta = mock.Moves.Count - movesBefore;
                bool noApply = form.UiSmokeQuickApplyCount == qBefore && form.UiSmokeHotkeyInvocationCount == invBefore;
                bool suppressed = form.UiSmokeHotkeySuppressedCount > supBefore;
                bool selectedSame = form.UiSmokeSelectedProfile()?.Id == selectedBefore;
                rec.Outcome = !noApply ? "applied" : suppressed ? "suppressed" : "not_received";
                rec.Note = $"selectedSame={selectedSame}";
                rec.Pass = rec.Dispatched && noApply && suppressed && rec.MovesDelta == 0
                           && rec.ActiveId == activeBefore && selectedSame;
                r.Steps.Add(rec);
            }, timeoutIsSuccess: true));
        }

        AddModalScenario("winforms_modal",
            form.UiSmokeShowOwnedModalForm,
            () => form.UiSmokeModalOpen && !form.UiSmokeOwnerEnabledNative,
            form.UiSmokeCloseOwnedModalForm);
        AddModalScenario("native_messagebox",
            form.UiSmokeShowNativeMessageBox,
            () => form.UiSmokeModalOpen && !form.UiSmokeOwnerEnabledNative,
            () => form.UiSmokeCloseOwnedNativeDialogs());

        // ---- Round 22: an already-registered key can be captured in the settings dialog ----
        {
            var capRec = new StepRecord { Name = "settings_dialog_capture_registered_key", Kind = Syn, ExpectedOutcome = "forwarded_to_capture", ExpectedMovesDelta = 0 };
            int q0 = 0, m0 = 0, fwd0 = 0;
            string? active0 = null;
            HotkeyConfig? cfg0 = null;
            IReadOnlyDictionary<HotkeyActionId, HotkeyStatus>? st0 = null;
            string? capturedText = null;
            steps.Add(new Step("open_settings_dialog", 4000,
                () =>
                {
                    q0 = form.UiSmokeQuickApplyCount;
                    m0 = mock.Moves.Count;
                    fwd0 = form.UiSmokeHotkeyForwardedCount;
                    active0 = monitor.ActiveProfile?.Id;
                    cfg0 = form.UiSmokeHotkeyConfig;
                    st0 = form.UiSmokeHotkeyStatuses;
                    form.BeginInvoke(new Action(form.UiSmokeOpenHotkeySettings));
                },
                () => form.UiSmokeOpenHotkeyDialog is { Visible: true },
                done => { if (!done) r.Timeouts.Add("open_settings_dialog"); }));
            steps.Add(new Step("forward_slot1_key_into_slot2_field", 2000,
                () =>
                {
                    form.UiSmokeOpenHotkeyDialog?.UiSmokeFocusCapture(1); // slot 2's key field
                    capRec.Dispatched = form.UiSmokePostSyntheticHotkey(HotkeyActionId.Slot1); // Ctrl+Alt+Shift+F23, registered
                },
                () => form.UiSmokeHotkeyForwardedCount > fwd0,
                _ => capturedText = form.UiSmokeOpenHotkeyDialog?.UiSmokeCaptureBoxes[1].Text));
            steps.Add(new Step("cancel_settings_dialog", 3000,
                () => form.UiSmokeOpenHotkeyDialog?.UiSmokeCancelButton.PerformClick(),
                () => form.UiSmokeOpenHotkeyDialog is null,
                done =>
                {
                    capRec.Completed = done;
                    capRec.ExpectedActiveId = active0;
                    capRec.ActiveId = monitor.ActiveProfile?.Id;
                    capRec.MonitoringOn = monitor.IsMonitoring;
                    capRec.MovesDelta = mock.Moves.Count - m0;
                    bool forwarded = form.UiSmokeHotkeyForwardedCount > fwd0;
                    bool captured = capturedText == "Ctrl+Alt+Shift+F23";
                    bool noApply = form.UiSmokeQuickApplyCount == q0;
                    bool cfgSame = cfg0 != null && form.UiSmokeHotkeyConfig!.ContentEquals(cfg0);
                    var st1 = form.UiSmokeHotkeyStatuses!;
                    bool statusSame = st0 != null && HotkeyActions.All.All(a => st0[a] == st1[a]);
                    capRec.Outcome = forwarded && captured ? "forwarded_to_capture" : "not_forwarded";
                    capRec.Note = $"captured='{capturedText}' cfgSame={cfgSame} statusSame={statusSame}";
                    capRec.Pass = done && capRec.Dispatched && forwarded && captured && noApply && cfgSame && statusSame
                                  && capRec.MovesDelta == 0 && capRec.ActiveId == active0;
                    r.Steps.Add(capRec);
                }));
        }

        steps.Add(new Step("profile_list_order", 200, () => { }, () => true, _ =>
        {
            r.ProfileListIds = form.UiSmokeProfileListIds.ToList();
            r.ProfileListOrderOk = r.ProfileListIds.SequenceEqual(new[] { "hk-a", "hk-b", "hk-b2", "hk-c" });
        }));

        // Configuring hotkeys must not move windows (monitoring is on here).
        int commitMovesBefore = 0;
        steps.Add(new Step("commit_enable_slot5", 1800,
            () =>
            {
                commitMovesBefore = mock.Moves.Count;
                var c = form.UiSmokeHotkeyConfig!;
                SetBinding(c, HotkeyActionId.Slot5, "hk-a", true, Keys.F20);
                var res = form.PerformHotkeyCommit(c);
                r.CommitEnableOutcome = res.Outcome.ToString();
                r.CommitEnableSlot5Status = res.Statuses.TryGetValue(HotkeyActionId.Slot5, out var s) ? s.State.ToString() : null;
            },
            () => false, // wait the full window so the real monitoring timer ticks several times
            _ =>
            {
                r.CommitEnableMovesDelta = mock.Moves.Count - commitMovesBefore;
                r.CommitEnableOk = r.CommitEnableOutcome == nameof(HotkeyCommitOutcome.Applied)
                                   && r.CommitEnableSlot5Status == nameof(HotkeyRegState.Registered)
                                   && r.CommitEnableMovesDelta == 0;
            }, timeoutIsSuccess: true));

        // Real file lock: save fails → previous config, registrations and bytes preserved.
        steps.Add(new Step("commit_locked_file_failure", 200, () =>
        {
            var before = form.UiSmokeHotkeyConfig!;
            var statusesBefore = form.UiSmokeHotkeyStatuses!;
            var bytesBefore = File.ReadAllBytes(hotkeyStore.FilePath);
            var proposed = before.Clone();
            proposed.Get(HotkeyActionId.Slot1).Enabled = false;
            proposed.Get(HotkeyActionId.Slot6).Enabled = true;
            HotkeyCommitResult res;
            using (new FileStream(hotkeyStore.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                res = form.PerformHotkeyCommit(proposed);
            }
            var after = form.UiSmokeHotkeyConfig!;
            var statusesAfter = form.UiSmokeHotkeyStatuses!;
            r.LockedCommitOutcome = res.Outcome.ToString();
            r.LockedCommitConfigUnchanged = after.ContentEquals(before);
            r.LockedCommitStatusUnchanged = HotkeyActions.All.All(a => statusesBefore[a] == statusesAfter[a]);
            r.LockedCommitFileUnchanged = bytesBefore.AsSpan().SequenceEqual(File.ReadAllBytes(hotkeyStore.FilePath));
            r.LockedCommitOk = res.Outcome == HotkeyCommitOutcome.SaveFailed && r.LockedCommitConfigUnchanged
                               && r.LockedCommitStatusUnchanged && r.LockedCommitFileUnchanged;
        }, () => true, _ => { }));
        // Proves slot1 is still registered after the failed save (unregistered ids are ignored).
        steps.Add(Quick("synthetic_slot1_after_failed_save", Syn, () => form.UiSmokePostSyntheticHotkey(HotkeyActionId.Slot1), QuickApplyOutcome.Applied, "hk-b", 1, "hk-b"));

        steps.Add(new Step("dialog_layout", 200, () =>
        {
            var sorted = ProfileOrdering.Sort(profiles.LoadAll(out _));
            using var dlg = new HotkeySettingsDialog(form.UiSmokeHotkeyConfig!, sorted, form.UiSmokeHotkeyStatuses!);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(60, 60);
            dlg.Show();
            Application.DoEvents();
            r.DialogDefaultLayoutOk = CheckDialog(dlg, out var d1);
            Snapshot(dlg, Path.Combine(outDir, outBase + ".dialog-default.png"));
            dlg.Size = dlg.MinimumSize;
            Application.DoEvents();
            r.DialogMinLayoutOk = CheckDialog(dlg, out var d2);
            Snapshot(dlg, Path.Combine(outDir, outBase + ".dialog-min.png"));
            r.DialogDetail = $"default:{d1} min:{d2}";
            dlg.Close();
        }, () => true, _ => { }));

        steps.Add(new Step("main_min_layout", 200, () =>
        {
            form.Size = form.MinimumSize;
            Application.DoEvents();
            var client = form.RectangleToScreen(form.ClientRectangle);
            var problems = new List<string>();
            foreach (var b in form.UiSmokeQuickButtons)
            {
                var rect = b.RectangleToScreen(b.ClientRectangle);
                if (!b.Visible || b.Width < 48 || b.Height < 20 || !client.Contains(rect)) problems.Add($"{b.AccessibleName}:{rect}");
            }
            for (int i = 1; i <= 8; i++)
            {
                if (!form.UiSmokeSlotButtonText(i).StartsWith(i.ToString())) problems.Add($"slot{i}_number_missing");
            }
            r.MainMinLayoutOk = problems.Count == 0;
            r.MainMinDetail = problems.Count == 0 ? "ok" : string.Join(";", problems);
            Snapshot(form, Path.Combine(outDir, outBase + ".main-min.png"));
        }, () => true, _ => { }));

        if (waitOsSeconds > 0)
        {
            int invBefore = 0;
            steps.Add(new Step("os_keyboard_dispatch", waitOsSeconds * 1000,
                () =>
                {
                    invBefore = form.UiSmokeHotkeyInvocationCount;
                    CliCommon.WriteJson(Path.Combine(outDir, outBase + ".ready.json"), new
                    {
                        ready = true,
                        press = "Ctrl+Alt+Shift+F23",
                        action = "slot1",
                        expectedProfileId = "hk-b",
                        waitSeconds = waitOsSeconds,
                        note = "Send this key combination via the OS keyboard path; the harness posts nothing during this step."
                    });
                },
                () => form.UiSmokeHotkeyInvocationCount > invBefore,
                done => r.OsDispatch = done ? "observed" : "not_observed",
                timeoutIsSuccess: true));
        }

        steps.Add(new Step("exit", 2000, () => form.TrayExitItem.PerformClick(), () => false, _ => { }, timeoutIsSuccess: true));

        // ---------- runner ----------
        var overallDeadline = DateTime.UtcNow.AddSeconds(90 + waitOsSeconds);
        int index = 0;
        Step? current = null;
        DateTime started = default;
        bool inTick = false;
        var runner = new System.Windows.Forms.Timer { Interval = 150 };
        runner.Tick += (_, _) =>
        {
            if (inTick) return;
            inTick = true;
            try
            {
                if (DateTime.UtcNow > overallDeadline)
                {
                    r.Timeouts.Add("overall_deadline");
                    runner.Stop();
                    // Close only our own modals (owned by this form, on this thread) before exiting.
                    try { form.UiSmokeCloseOwnedModalForm(); } catch { }
                    try { form.UiSmokeCloseOwnedNativeDialogs(); } catch { }
                    try { form.UiSmokeOpenHotkeyDialog?.UiSmokeCancelButton.PerformClick(); } catch { }
                    form.BeginInvoke(new Action(() => form.TrayExitItem.PerformClick()));
                    return;
                }
                if (current is null)
                {
                    if (index >= steps.Count) { runner.Stop(); return; }
                    current = steps[index++];
                    started = DateTime.UtcNow;
                    current.Start();
                    return;
                }
                bool done = current.Done();
                bool timedOut = (DateTime.UtcNow - started).TotalMilliseconds >= current.TimeoutMs;
                if (done || timedOut)
                {
                    if (!done && timedOut && !current.TimeoutIsSuccess) r.Timeouts.Add(current.Name);
                    current.Finish(done);
                    current = null;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.ToString();
                runner.Stop();
                try { form.TrayExitItem.PerformClick(); } catch { }
            }
            finally { inTick = false; }
        };
        runner.Start();
        Application.Run(form);
        r.ApplicationRunReturned = true;

        // ---------- registrations released after exit? ----------
        try
        {
            using var probe = new GlobalHotkeyWindow();
            bool allFree = true;
            int pid = 100;
            foreach (var key in new[] { Keys.F20, Keys.F21, Keys.F22, Keys.F23 })
            {
                uint mods = HotkeyRules.MOD_CONTROL | HotkeyRules.MOD_ALT | HotkeyRules.MOD_SHIFT | HotkeyRules.MOD_NOREPEAT;
                bool ok = probe.TryRegister(pid, mods, (uint)key, out var err);
                r.ReleaseProbe[$"Ctrl+Alt+Shift+{key}"] = ok ? "free" : $"busy:{err}";
                if (ok) probe.Unregister(pid);
                allFree &= ok;
                pid++;
            }
            r.ReleasedAfterExit = allFree;
        }
        catch (Exception ex)
        {
            r.ReleaseProbe["error"] = ex.GetType().Name;
        }

        bool osOk = waitOsSeconds == 0 || r.OsDispatch == "observed";
        r.Ok = r.Error == null
               && r.Timeouts.Count == 0
               && r.HotkeyLoadError == null
               && r.StartupHidden && r.StartupMoves == 0 && r.StartupActiveId == null && !r.StartupMonitoring
               && r.StartupRegistrationsOk && r.StartupSlotTextsOk
               && r.Steps.Count > 0 && r.Steps.All(s => s.Pass)
               && r.ProfileListOrderOk
               && r.CommitEnableOk
               && r.LockedCommitOk
               && r.DialogDefaultLayoutOk && r.DialogMinLayoutOk
               && r.MainMinLayoutOk
               && r.ApplicationRunReturned
               && r.ReleasedAfterExit
               && osOk;
        CliCommon.WriteJson(outPath, r);
        return r.Ok ? 0 : 1;

        Step IgnoredSynthetic(string name, HotkeyActionId action)
        {
            var rec = new StepRecord { Name = name, Kind = Syn, ExpectedOutcome = "ignored", ExpectedMovesDelta = 0 };
            int countBefore = 0, invBefore = 0, movesBefore = 0;
            string? activeBefore = null;
            return new Step(name, 1200,
                () =>
                {
                    countBefore = form.UiSmokeQuickApplyCount;
                    invBefore = form.UiSmokeHotkeyInvocationCount;
                    movesBefore = mock.Moves.Count;
                    activeBefore = monitor.ActiveProfile?.Id;
                    rec.ExpectedActiveId = activeBefore;
                    rec.Dispatched = form.UiSmokePostSyntheticHotkey(action);
                },
                () => false,
                _ =>
                {
                    rec.Completed = true;
                    rec.Outcome = form.UiSmokeQuickApplyCount == countBefore && form.UiSmokeHotkeyInvocationCount == invBefore ? "ignored" : "handled";
                    rec.ActiveId = monitor.ActiveProfile?.Id;
                    rec.MonitoringOn = monitor.IsMonitoring;
                    rec.MovesDelta = mock.Moves.Count - movesBefore;
                    rec.Pass = rec.Dispatched && rec.Outcome == "ignored" && rec.MovesDelta == 0 && rec.ActiveId == activeBefore;
                    r.Steps.Add(rec);
                }, timeoutIsSuccess: true);
        }
    }

    private static bool CheckDialog(HotkeySettingsDialog d, out string detail)
    {
        var problems = new List<string>();
        if (d.UiSmokeCaptureBoxes.Count != 10) problems.Add("capture_count");
        if (d.UiSmokeEnableBoxes.Count != 10) problems.Add("enable_count");
        if (d.UiSmokeProfileCombos.Count != 8) problems.Add("combo_count");
        var client = d.RectangleToScreen(d.ClientRectangle);
        var all = d.UiSmokeCaptureBoxes.Cast<Control>()
            .Concat(d.UiSmokeEnableBoxes)
            .Concat(d.UiSmokeProfileCombos)
            .Concat(new Control[] { d.UiSmokeOkButton, d.UiSmokeCancelButton });
        foreach (var c in all)
        {
            var rect = c.RectangleToScreen(c.ClientRectangle);
            if (!c.Visible || !c.Enabled || c.Width <= 0 || c.Height <= 0 || !client.Contains(rect))
                problems.Add($"{c.AccessibleName ?? c.Text}:{rect}");
            if (c is not Button && string.IsNullOrWhiteSpace(c.AccessibleName)) problems.Add("no_accessible_name");
        }
        // Row alignment: in every one of the 10 rows the "사용" checkbox and the key field must share
        // a vertical center (the last row used to stretch and misalign), and consecutive rows must
        // be evenly spaced.
        if (d.UiSmokeCaptureBoxes.Count == 10 && d.UiSmokeEnableBoxes.Count == 10)
        {
            int? prevCenter = null;
            for (int i = 0; i < 10; i++)
            {
                var capR = d.UiSmokeCaptureBoxes[i].RectangleToScreen(d.UiSmokeCaptureBoxes[i].ClientRectangle);
                var chkR = d.UiSmokeEnableBoxes[i].RectangleToScreen(d.UiSmokeEnableBoxes[i].ClientRectangle);
                int capCenter = capR.Top + capR.Height / 2;
                int chkCenter = chkR.Top + chkR.Height / 2;
                if (Math.Abs(capCenter - chkCenter) > 8) problems.Add($"row{i + 1}_misaligned:{capCenter}vs{chkCenter}");
                if (chkR.Height > 40) problems.Add($"row{i + 1}_stretched:{chkR.Height}");
                if (prevCenter is int p && Math.Abs(capCenter - p - 34) > 4) problems.Add($"row{i + 1}_spacing:{capCenter - p}");
                prevCenter = capCenter;
            }
        }
        detail = problems.Count == 0 ? "ok" : string.Join(";", problems.Take(8));
        return problems.Count == 0;
    }

    private static void Snapshot(Control c, string path)
    {
        try
        {
            using var bmp = new Bitmap(c.Width, c.Height);
            c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }

    private static void SetBinding(HotkeyConfig cfg, HotkeyActionId action, string? profileId, bool enabled, Keys key)
    {
        var b = cfg.Get(action);
        if (HotkeyActions.IsSlot(action)) b.ProfileId = profileId;
        b.Enabled = enabled;
        b.Ctrl = true;
        b.Alt = true;
        b.Shift = true;
        b.Key = key;
    }

    private static Profile MakeProfile(string id, string name)
    {
        var rect = ProfileRects[id];
        return new Profile
        {
            Id = id,
            Name = name,
            Entries =
            {
                new ProfileEntry
                {
                    Id = "e-" + id,
                    Role = "hk-role",
                    ExePath = @"C:\x\hk.exe",
                    ProcessName = "hk",
                    ClassName = "HkClass",
                    TitleContains = "hk-window",
                    Rect = new SavedRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height }
                }
            }
        };
    }

    private sealed class Step
    {
        public string Name { get; }
        public int TimeoutMs { get; }
        public Action Start { get; }
        public Func<bool> Done { get; }
        public Action<bool> Finish { get; }
        public bool TimeoutIsSuccess { get; }

        public Step(string name, int timeoutMs, Action start, Func<bool> done, Action<bool> finish, bool timeoutIsSuccess = false)
        {
            Name = name; TimeoutMs = timeoutMs; Start = start; Done = done; Finish = finish; TimeoutIsSuccess = timeoutIsSuccess;
        }
    }

    /// <summary>In-memory provider; never touches native windows.</summary>
    private sealed class MockProvider : IWindowProvider
    {
        public List<WindowInfo> Windows { get; } = new();
        public HashSet<WindowIdentity> LiveIdentities { get; } = new();
        public List<(WindowIdentity id, WindowRect target, bool max)> Moves { get; } = new();
        private readonly List<MonitorInfo> _monitors = new()
        {
            new MonitorInfo(@"\\.\DISPLAY1", new WindowRect(0, 0, 1920, 1080), new WindowRect(0, 0, 1920, 1040), true)
        };

        public void AddWindow(WindowInfo w)
        {
            Windows.Add(w);
            LiveIdentities.Add(w.Identity);
        }

        public EnumerationResult Enumerate() => new()
        {
            Windows = Windows.ToList(),
            Excluded = Array.Empty<ExcludedWindow>(),
            Monitors = _monitors.ToList()
        };

        public bool IsAlive(WindowIdentity id) => LiveIdentities.Contains(id);
        public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => LiveIdentities.ToList();
        public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata() => Windows.ToList();

        public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string exeKey, string? className = null)
        {
            int ix = Windows.FindIndex(x => x.Identity.Equals(id));
            if (ix < 0) return new MoveResult(MoveOutcome.WindowGone, default, "gone");
            Moves.Add((id, target, maximize));
            var win = Windows[ix];
            Windows[ix] = new WindowInfo
            {
                Identity = win.Identity,
                ExePath = win.ExePath,
                ProcessName = win.ProcessName,
                ClassName = win.ClassName,
                Title = win.Title,
                CurrentRect = target,
                NormalRect = win.NormalRect,
                ShowState = maximize ? WindowShowState.Maximized : WindowShowState.Normal,
                MonitorDevice = win.MonitorDevice
            };
            return new MoveResult(MoveOutcome.Success, target, null);
        }
    }
}
