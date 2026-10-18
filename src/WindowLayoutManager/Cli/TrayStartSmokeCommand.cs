using System.Diagnostics;
using WindowLayoutManager.App;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

/// <summary>
/// Focused tray-startup lifecycle smoke. Fresh temp data dir, mock IWindowProvider, exercises the
/// EXACT SAME MainForm(startInTray:true) path that Program.RunGui(--start-in-tray) uses.
///
/// Design points enforced per Codex round-17 review:
///  - The newly-added B window matches a DISJOINT role (role-B, title "new-only") that is initially
///    empty, so the reserved role-A slot stays occupied by the pre-existing A. The matcher must
///    place B exactly once at role-B's target; the engine's correct SlotOccupied behavior is
///    additionally proved by phase 3 which adds a competing-A window and asserts it is NOT moved.
///  - No direct monitor.Tick() calls. The harness waits for the real MainForm._timer (800ms) to
///    drive monitoring. Each phase has its own deadline; total case bound is ~20s.
///  - CaseResult members are public properties so System.Text.Json (which does not IncludeFields)
///    serializes every observation.
///  - Shown and VisibleChanged→true event counts are captured BEFORE the intentional tray Open so
///    "startup hidden" vs "show-then-hide flash" can be distinguished.
///  - After Application.Run returns we assert the tray icon is no longer visible and the monitoring
///    timer is stopped, confirming the exit lifecycle actually ran.
/// </summary>
public static class TrayStartSmokeCommand
{
    public static int Run(string[] args)
    {
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }

        var dataDirA = Path.Combine(Path.GetTempPath(), "wlm-tray-start-" + Guid.NewGuid().ToString("N"));
        var dataDirB = Path.Combine(Path.GetTempPath(), "wlm-tray-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirA);
        Directory.CreateDirectory(dataDirB);

        var caseEnabled = RunCase(dataDirA, persistMonitoringEnabled: true);
        var caseDisabled = RunCase(dataDirB, persistMonitoringEnabled: false);

        bool ok = caseEnabled.Ok && caseDisabled.Ok;
        CliCommon.WriteJson(outPath, new
        {
            ok,
            caseMonitoringTrue = caseEnabled,
            caseMonitoringFalse = caseDisabled
        });
        return ok ? 0 : 1;
    }

    public sealed class CaseResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? TimeoutInPhase { get; set; }

        // Phase 0 — immediately after Bootstrap
        public bool FormVisibleAtStart { get; set; }
        public bool ShowInTaskbarAtStart { get; set; }
        public bool TrayVisibleAtStart { get; set; }
        public bool TimerEnabledAtStart { get; set; }
        public bool ActiveProfileLoaded { get; set; }
        public bool MonitoringStateAtStart { get; set; }
        public int MovesAtStart { get; set; }
        public int ShownCountAtStart { get; set; }
        public int VisibleTrueTransitionsAtStart { get; set; }

        // Phase 1 — real timer runs while only pre-existing A is alive
        public int MovesAfterIdleWait { get; set; }
        public int EnumCallsDuringIdle { get; set; }

        // Phase 2 — add disjoint B (role-B) and wait for the real timer to place it
        public int MovesAfterB { get; set; }
        public bool BTargetVerified { get; set; }
        public int EnumCallsDuringBWait { get; set; }
        public bool AUntouchedAfterB { get; set; }

        // Phase 3 — add competing-A (title matches reserved role-A); must stay SlotOccupied
        public int MovesAfterCompetingA { get; set; }
        public bool AStillUntouchedAfterCompetingA { get; set; }

        // Phase 4 — Tray Open shows a normal usable form.
        // *BeforeOpen counters are snapshotted IMMEDIATELY before the user-driven Open so the
        // "no flash during the hidden phase" assertion covers the entire time from startup through
        // competing-A wait. *AfterOpen counters are polled on later message turns because WinForms
        // posts Shown asynchronously after Show().
        public int ShownCountBeforeOpen { get; set; }
        public int VisibleTrueTransitionsBeforeOpen { get; set; }
        public bool FormVisibleAfterOpen { get; set; }
        public bool ShowInTaskbarAfterOpen { get; set; }
        public int ShownCountAfterOpen { get; set; }
        public int VisibleTrueTransitionsAfterOpen { get; set; }

        // Phase 5 — HideToTray hides again
        public bool FormVisibleAfterHide { get; set; }

        // Phase 6 — ExitFromTray closes the message loop
        public bool ExitRequested { get; set; }

        // Post-Application.Run observations
        public bool ApplicationRunReturned { get; set; }
        public bool TrayVisibleAfterExit { get; set; }
        public bool TimerEnabledAfterExit { get; set; }
    }

    private static CaseResult RunCase(string dataDir, bool persistMonitoringEnabled)
    {
        var r = new CaseResult();
        var profiles = new ProfileStore(dataDir);
        // Two disjoint roles: role-A matches the pre-existing fixture and stays reserved; role-B
        // starts empty and will be filled by a brand-new matching window spawned during the test.
        var profile = new Profile
        {
            Id = "traysmoke-profile",
            Name = "Tray Start Smoke",
            Entries =
            {
                new ProfileEntry
                {
                    Id = "role-A",
                    Role = "role-A",
                    ExePath = @"C:\x\probe.exe",
                    ProcessName = "probe",
                    ClassName = "ProbeClass",
                    TitleContains = "preexisting-A",
                    Rect = new SavedRect { X = 100, Y = 120, Width = 400, Height = 300 }
                },
                new ProfileEntry
                {
                    Id = "role-B",
                    Role = "role-B",
                    ExePath = @"C:\x\probe.exe",
                    ProcessName = "probe",
                    ClassName = "ProbeClass",
                    TitleContains = "new-only",
                    Rect = new SavedRect { X = 640, Y = 440, Width = 500, Height = 360 }
                }
            }
        };
        profiles.Save(profile);
        var settings = new SettingsStore(dataDir);
        settings.Save(new AppSettings
        {
            ActiveProfileId = profile.Id,
            MonitoringEnabled = persistMonitoringEnabled
        });

        var mock = new MockProvider();
        // Pre-existing A matching role-A exactly. Must NOT be moved at startup; its initial rect
        // (300, 300, 400, 300) does NOT equal role-A's saved target, so any movement would be
        // visible as a rect change too.
        var preIdA = new WindowIdentity(new IntPtr(1), 101, 1000);
        var preRectA = new WindowRect(300, 300, 400, 300);
        mock.AddWindow(new WindowInfo
        {
            Identity = preIdA,
            ExePath = @"C:\x\probe.exe",
            ProcessName = "probe",
            ClassName = "ProbeClass",
            Title = "preexisting-A window",
            CurrentRect = preRectA,
            NormalRect = preRectA,
            ShowState = WindowShowState.Normal,
            MonitorDevice = @"\\.\DISPLAY1"
        });

        var monitor = new MonitoringService(mock, profiles, settings);
        monitor.InitializeFromSettings();

        var form = new MainForm(profiles, settings, monitor, mock, startInTray: true);
        form.UiSmokeSuppressDialogs(true);

        // Shown / VisibleChanged→true counters BEFORE Application.Run. Must stay 0 through the
        // suppressed-start lifecycle and only increment after a user-driven Open.
        int shownCount = 0;
        int visibleTrueTransitions = 0;
        form.Shown += (_, _) => shownCount++;
        form.VisibleChanged += (_, _) => { if (form.Visible) visibleTrueTransitions++; };

        // Identity of the window we'll inject in phase 2 (role-B).
        var newBId = new WindowIdentity(new IntPtr(2), 202, 2000);
        var newBInitialRect = new WindowRect(777, 777, 220, 180);
        // Identity for the phase-3 competing-A window (same title predicate as role-A).
        var competingAId = new WindowIdentity(new IntPtr(3), 303, 3000);
        var competingAInitialRect = new WindowRect(50, 50, 400, 300);

        // Per-case overall deadline (safety net): 20s per the review's guidance.
        var caseDeadline = DateTime.UtcNow.AddSeconds(20);

        // Phase state machine. Each phase sets its own deadline when entered.
        const int P_RECORD_START = 0;
        const int P_IDLE_WAIT = 1;        // ~2.5s with no new window, assert no moves
        const int P_ADD_B = 2;            // inject new-only B
        const int P_WAIT_B_MOVED = 3;     // wait up to 6s for real timer to place B
        const int P_ADD_COMPETING_A = 4;  // inject title-matches-A competing window
        const int P_WAIT_COMPETING_A = 5; // wait ~2s, assert no additional moves
        const int P_OPEN = 6;             // snapshot pre-open counters, PerformClick
        const int P_WAIT_SHOWN = 7;       // wait up to 2s for async Shown / VisibleChanged → true
        const int P_HIDE = 8;
        const int P_EXIT = 9;
        int phase = P_RECORD_START;
        DateTime phaseDeadline = DateTime.UtcNow.AddSeconds(2);

        int enumCountAtPhaseStart = 0;

        var fireTimer = new System.Windows.Forms.Timer { Interval = 200 };
        bool inTick = false;

        void SafeFail(string phaseLabel)
        {
            if (r.TimeoutInPhase == null) r.TimeoutInPhase = phaseLabel;
        }

        fireTimer.Tick += (_, _) =>
        {
            if (inTick) return;
            inTick = true;
            try
            {
                // Case-level safety net: if we've gone way beyond 20s, force exit so the test
                // process never hangs.
                if (DateTime.UtcNow > caseDeadline && phase < P_EXIT)
                {
                    SafeFail($"case_deadline_phase={phase}");
                    phase = P_EXIT;
                }

                switch (phase)
                {
                    case P_RECORD_START:
                        r.FormVisibleAtStart = form.Visible;
                        r.ShowInTaskbarAtStart = form.ShowInTaskbar;
                        r.TrayVisibleAtStart = form.UiSmokeTrayVisible;
                        r.TimerEnabledAtStart = form.UiSmokeTimerEnabled;
                        r.ActiveProfileLoaded = monitor.ActiveProfile?.Id == profile.Id;
                        r.MonitoringStateAtStart = monitor.IsMonitoring;
                        r.MovesAtStart = mock.Moves.Count;
                        r.ShownCountAtStart = shownCount;
                        r.VisibleTrueTransitionsAtStart = visibleTrueTransitions;
                        phase = P_IDLE_WAIT;
                        phaseDeadline = DateTime.UtcNow.AddMilliseconds(2500);
                        enumCountAtPhaseStart = mock.EnumerateCount;
                        break;

                    case P_IDLE_WAIT:
                        // Real timer should tick several times with no new window; must not move A.
                        if (DateTime.UtcNow >= phaseDeadline)
                        {
                            r.MovesAfterIdleWait = mock.Moves.Count;
                            r.EnumCallsDuringIdle = mock.EnumerateCount - enumCountAtPhaseStart;
                            phase = P_ADD_B;
                        }
                        break;

                    case P_ADD_B:
                        mock.AddWindow(new WindowInfo
                        {
                            Identity = newBId,
                            ExePath = @"C:\x\probe.exe",
                            ProcessName = "probe",
                            ClassName = "ProbeClass",
                            Title = "a new-only window",
                            CurrentRect = newBInitialRect,
                            NormalRect = newBInitialRect,
                            ShowState = WindowShowState.Normal,
                            MonitorDevice = @"\\.\DISPLAY1"
                        });
                        phase = P_WAIT_B_MOVED;
                        phaseDeadline = DateTime.UtcNow.AddSeconds(6);
                        enumCountAtPhaseStart = mock.EnumerateCount;
                        break;

                    case P_WAIT_B_MOVED:
                    {
                        // Observe mock.Moves — populated by the production pipeline when the real
                        // MainForm._timer fires monitor.Tick. No direct monitor.Tick here.
                        // WindowIdentity is a record CLASS, so FirstOrDefault on an empty match
                        // yields a (null, default, false) tuple. Probe existence first via Any,
                        // then look the entry up only when known present.
                        bool bMoved = mock.Moves.Any(m => m.id != null && m.id.Equals(newBId));
                        bool aUntouched = !mock.Moves.Any(m => m.id != null && m.id.Equals(preIdA));
                        bool reached;
                        if (persistMonitoringEnabled)
                        {
                            reached = bMoved && aUntouched;
                        }
                        else
                        {
                            // Monitoring paused — nothing should ever move. Wait the full phase
                            // deadline to prove quiescence.
                            reached = DateTime.UtcNow >= phaseDeadline && mock.Moves.Count == 0;
                        }
                        if (reached)
                        {
                            r.MovesAfterB = mock.Moves.Count;
                            r.AUntouchedAfterB = aUntouched;
                            r.EnumCallsDuringBWait = mock.EnumerateCount - enumCountAtPhaseStart;
                            if (bMoved)
                            {
                                var bMoveEntry = mock.Moves.First(m => m.id != null && m.id.Equals(newBId));
                                r.BTargetVerified = Math.Abs(bMoveEntry.target.X - 640) <= 4
                                                 && Math.Abs(bMoveEntry.target.Y - 440) <= 4
                                                 && Math.Abs(bMoveEntry.target.Width - 500) <= 4
                                                 && Math.Abs(bMoveEntry.target.Height - 360) <= 4;
                            }
                            phase = P_ADD_COMPETING_A;
                            phaseDeadline = DateTime.UtcNow.AddMilliseconds(500);
                        }
                        else if (DateTime.UtcNow >= phaseDeadline)
                        {
                            // Timed out waiting for B. For monitoring=true this is a failure; for
                            // monitoring=false this is actually the success path handled above.
                            r.MovesAfterB = mock.Moves.Count;
                            r.AUntouchedAfterB = aUntouched;
                            r.EnumCallsDuringBWait = mock.EnumerateCount - enumCountAtPhaseStart;
                            if (persistMonitoringEnabled) SafeFail("wait_b_moved");
                            phase = P_ADD_COMPETING_A;
                            phaseDeadline = DateTime.UtcNow.AddMilliseconds(500);
                        }
                        break;
                    }

                    case P_ADD_COMPETING_A:
                        mock.AddWindow(new WindowInfo
                        {
                            Identity = competingAId,
                            ExePath = @"C:\x\probe.exe",
                            ProcessName = "probe",
                            ClassName = "ProbeClass",
                            Title = "another preexisting-A clone",
                            CurrentRect = competingAInitialRect,
                            NormalRect = competingAInitialRect,
                            ShowState = WindowShowState.Normal,
                            MonitorDevice = @"\\.\DISPLAY1"
                        });
                        phase = P_WAIT_COMPETING_A;
                        phaseDeadline = DateTime.UtcNow.AddSeconds(3);
                        break;

                    case P_WAIT_COMPETING_A:
                    {
                        // Wait the full window; competing-A must stay unmoved because role-A is
                        // reserved by the pre-existing A. We also assert A itself is still untouched.
                        if (DateTime.UtcNow >= phaseDeadline)
                        {
                            int movesForCompeting = mock.Moves.Count(m => m.id.Equals(competingAId));
                            r.MovesAfterCompetingA = movesForCompeting;
                            r.AStillUntouchedAfterCompetingA = !mock.Moves.Any(m => m.id.Equals(preIdA));
                            phase = P_OPEN;
                        }
                        break;
                    }

                    case P_OPEN:
                        // Snapshot counters immediately BEFORE Open — covers the entire hidden
                        // phase from startup through competing-A wait. Expected 0 (no flash).
                        r.ShownCountBeforeOpen = shownCount;
                        r.VisibleTrueTransitionsBeforeOpen = visibleTrueTransitions;
                        form.TrayOpenItem.PerformClick();
                        // form.Visible / ShowInTaskbar are synchronous property reads — OK here.
                        r.FormVisibleAfterOpen = form.Visible;
                        r.ShowInTaskbarAfterOpen = form.ShowInTaskbar;
                        // WinForms Shown is posted asynchronously; poll on later message turns.
                        phase = P_WAIT_SHOWN;
                        phaseDeadline = DateTime.UtcNow.AddSeconds(2);
                        break;

                    case P_WAIT_SHOWN:
                        if (shownCount >= 1 && visibleTrueTransitions >= 1)
                        {
                            r.ShownCountAfterOpen = shownCount;
                            r.VisibleTrueTransitionsAfterOpen = visibleTrueTransitions;
                            phase = P_HIDE;
                            phaseDeadline = DateTime.UtcNow.AddMilliseconds(300);
                        }
                        else if (DateTime.UtcNow >= phaseDeadline)
                        {
                            r.ShownCountAfterOpen = shownCount;
                            r.VisibleTrueTransitionsAfterOpen = visibleTrueTransitions;
                            SafeFail("wait_shown");
                            phase = P_HIDE;
                            phaseDeadline = DateTime.UtcNow.AddMilliseconds(300);
                        }
                        break;

                    case P_HIDE:
                        form.UiSmokeTriggerHide();
                        r.FormVisibleAfterHide = form.Visible;
                        phase = P_EXIT;
                        phaseDeadline = DateTime.UtcNow.AddMilliseconds(300);
                        break;

                    case P_EXIT:
                        r.ExitRequested = true;
                        fireTimer.Stop();
                        form.TrayExitItem.PerformClick();
                        break;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.ToString();
                fireTimer.Stop();
                try { form.TrayExitItem.PerformClick(); } catch { }
            }
            finally { inTick = false; }
        };

        // Pre-arm so the first Tick fires right after Application.Run starts the message pump.
        fireTimer.Start();
        Application.Run(form);
        r.ApplicationRunReturned = true;
        r.TrayVisibleAfterExit = form.UiSmokeTrayVisible;
        r.TimerEnabledAfterExit = form.UiSmokeTimerEnabled;

        int expectedBMoves = persistMonitoringEnabled ? 1 : 0;
        bool bTargetOk = persistMonitoringEnabled ? r.BTargetVerified : (r.MovesAfterB == 0);

        r.Ok = r.Error == null
            && r.TimeoutInPhase == null
            && r.FormVisibleAtStart == false
            && r.ShowInTaskbarAtStart == false
            && r.TrayVisibleAtStart == true
            && r.TimerEnabledAtStart == true
            && r.ActiveProfileLoaded == true
            && r.MonitoringStateAtStart == persistMonitoringEnabled
            && r.MovesAtStart == 0
            && r.ShownCountAtStart == 0
            && r.VisibleTrueTransitionsAtStart == 0
            && r.MovesAfterIdleWait == 0
            && r.MovesAfterB == expectedBMoves
            && r.AUntouchedAfterB == true
            && bTargetOk
            && r.MovesAfterCompetingA == 0
            && r.AStillUntouchedAfterCompetingA == true
            && r.ShownCountBeforeOpen == 0
            && r.VisibleTrueTransitionsBeforeOpen == 0
            && r.FormVisibleAfterOpen == true
            && r.ShowInTaskbarAfterOpen == true
            && r.ShownCountAfterOpen >= 1
            && r.VisibleTrueTransitionsAfterOpen >= 1
            && r.FormVisibleAfterHide == false
            && r.ExitRequested == true
            && r.ApplicationRunReturned == true
            && r.TrayVisibleAfterExit == false
            && r.TimerEnabledAfterExit == false;
        return r;
    }

    private sealed class MockProvider : IWindowProvider
    {
        public List<WindowInfo> Windows { get; } = new();
        public HashSet<WindowIdentity> LiveIdentities { get; } = new();
        public List<MonitorInfo> Monitors { get; } = new() { new MonitorInfo(@"\\.\DISPLAY1",
            new WindowRect(0, 0, 1920, 1080), new WindowRect(0, 0, 1920, 1040), true) };
        public List<(WindowIdentity id, WindowRect target, bool max)> Moves { get; } = new();
        public int EnumerateCount { get; private set; }

        public void AddWindow(WindowInfo w)
        {
            Windows.Add(w);
            LiveIdentities.Add(w.Identity);
        }

        public EnumerationResult Enumerate()
        {
            EnumerateCount++;
            return new EnumerationResult
            {
                Windows = Windows.ToList(),
                Excluded = Array.Empty<ExcludedWindow>(),
                Monitors = Monitors.ToList()
            };
        }

        public bool IsAlive(WindowIdentity id) => LiveIdentities.Contains(id);

        public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => LiveIdentities.ToList();

        public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata() => Windows.ToList();

        public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string exeKey, string? className = null)
        {
            int ix = Windows.FindIndex(w => w.Identity.Equals(id));
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
