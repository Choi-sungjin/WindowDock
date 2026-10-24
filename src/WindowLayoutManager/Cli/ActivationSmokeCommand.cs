using System.Diagnostics;
using WindowLayoutManager.App;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

/// <summary>
/// --activation-smoke --output &lt;r&gt;
///
/// v0.2.1 checks in an isolated host: embedded icon on form + tray, versioned title, tray
/// "단축키 설정…" opens the settings dialog without applying/assigning/enabling anything, and the
/// second-launch behavior using REAL child processes (--activation-client) that run the exact
/// production decision <see cref="SingleInstanceActivation.HandleSecondInstance"/>:
///   - duplicate --start-in-tray → silent, the hidden app stays hidden, no signal sent;
///   - normal launch → the running (hidden) app shows its main window.
/// Uses a unique QA event name (never the production one), temp data and an in-memory window
/// provider; it cannot reach a real running WindowDock and touches no user window.
///
/// --activation-client --scope &lt;s&gt; [--start-in-tray] [--output &lt;r&gt;]
///   Exit 0 = ActivatedExisting, 10 = SilentAutostartDuplicate, 11 = ActivationUnavailable, 2 = bad args.
/// </summary>
public static class ActivationSmokeCommand
{
    public static int RunClient(string[] args)
    {
        var scope = CliCommon.GetArg(args, "--scope");
        var outPath = CliCommon.GetArg(args, "--output");
        bool tray = CliCommon.HasFlag(args, "--start-in-tray");
        if (string.IsNullOrEmpty(scope)) { Console.Error.WriteLine("--scope required"); return 2; }
        string name;
        try { name = SingleInstanceActivation.EventNameForScope(scope); }
        catch { Console.Error.WriteLine("invalid --scope"); return 2; }
        var outcome = SingleInstanceActivation.HandleSecondInstance(tray, name, TimeSpan.FromSeconds(3));
        if (!string.IsNullOrEmpty(outPath)) CliCommon.WriteJson(outPath, new { outcome = outcome.ToString(), startInTray = tray });
        return outcome switch
        {
            SecondInstanceOutcome.ActivatedExisting => 0,
            SecondInstanceOutcome.SilentAutostartDuplicate => 10,
            _ => 11
        };
    }

    public sealed class Result
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<string> Timeouts { get; set; } = new();

        public bool IconLoaded { get; set; }
        public string? IconLoadError { get; set; }
        public List<int> IconFrameSizes { get; set; } = new();
        public bool IconFramesOk { get; set; }
        public bool FormIconEmbedded { get; set; }
        public bool TrayIconEmbedded { get; set; }
        public bool TitleOk { get; set; }
        public string? Title { get; set; }
        public bool TraySettingsItemPresent { get; set; }

        public bool StartupHidden { get; set; }
        public int DupClientExit { get; set; } = -1;
        public bool DupStayedHidden { get; set; }
        public int SignalsAfterDup { get; set; } = -1;
        public int NormalClientExit { get; set; } = -1;
        public bool NormalShownVisible { get; set; }
        public bool NormalShowInTaskbar { get; set; }
        public int SignalsAfterNormal { get; set; } = -1;
        public int ExternalActivationsAfterNormal { get; set; } = -1;
        public bool HiddenAgain { get; set; }
        public int DupAgainClientExit { get; set; } = -1;
        public bool DupAgainStayedHidden { get; set; }
        public int SignalsAfterDupAgain { get; set; } = -1;
        public string? NoListenerOutcome { get; set; }
        public long NoListenerMs { get; set; }

        public bool TraySettingsDialogOpened { get; set; }
        public bool TraySettingsMainShown { get; set; }
        public bool TraySettingsNoApply { get; set; }
        public string? TraySettingsDetail { get; set; }

        public bool ApplicationRunReturned { get; set; }
        public bool ListenerReleased { get; set; }

        // Pre-handle startup window: a signal before the form handle/Bootstrap exists is queued.
        public bool PreHandleNoHandleAtRequest { get; set; }
        public bool PreHandleRequestAccepted { get; set; }
        public bool PreHandleNotShownBeforeRun { get; set; }
        public bool PreHandleShownAfterStartup { get; set; }
        public int PreHandleActivationCount { get; set; } = -1;
        public int PreHandleMoves { get; set; } = -1;
        public bool PreHandleOk { get; set; }
    }

    /// <summary>
    /// Deterministic check for the startup race: the listener exists before Application.Run, so a
    /// second launch may signal before the form has a handle. The request must be queued and shown
    /// once Bootstrap finishes — and nothing else (no apply, no move) may happen.
    /// </summary>
    private static void RunPreHandleCase(Result r)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "wlm-activation-prehandle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var profiles = new ProfileStore(dataDir);
        var settings = new SettingsStore(dataDir);
        settings.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = false });
        var mock = new MockProvider();
        var monitor = new MonitoringService(mock, profiles, settings);
        monitor.InitializeFromSettings();
        var form = new MainForm(profiles, settings, monitor, mock, startInTray: true, hotkeyStore: new HotkeyStore(dataDir));
        form.UiSmokeSuppressDialogs(true);

        r.PreHandleNoHandleAtRequest = !form.IsHandleCreated;
        r.PreHandleRequestAccepted = form.RequestExternalActivation(); // simulates the listener callback
        r.PreHandleNotShownBeforeRun = !form.Visible && form.UiSmokeExternalActivationCount == 0;

        var deadline = DateTime.UtcNow.AddSeconds(8);
        var t = new System.Windows.Forms.Timer { Interval = 150 };
        t.Tick += (_, _) =>
        {
            if (form.Visible || DateTime.UtcNow > deadline)
            {
                t.Stop();
                r.PreHandleShownAfterStartup = form.Visible;
                r.PreHandleActivationCount = form.UiSmokeExternalActivationCount;
                r.PreHandleMoves = mock.Moves.Count;
                form.TrayExitItem.PerformClick();
            }
        };
        t.Start();
        Application.Run(form);
        t.Dispose();
        r.PreHandleOk = r.PreHandleNoHandleAtRequest && r.PreHandleRequestAccepted && r.PreHandleNotShownBeforeRun
                        && r.PreHandleShownAfterStartup && r.PreHandleActivationCount == 1 && r.PreHandleMoves == 0;
    }

    public static int Run(string[] args)
    {
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }
        var exePath = Environment.ProcessPath ?? "";
        var r = new Result();
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        var outBase = Path.GetFileNameWithoutExtension(outPath);

        // Icon / identity (no window needed)
        r.IconLoaded = AppIcon.IsLoaded;
        r.IconLoadError = AppIcon.LoadError;
        r.IconFrameSizes = AppIcon.FrameSizes().ToList();
        r.IconFramesOk = new[] { 16, 20, 24, 32, 48, 64, 128, 256 }.All(r.IconFrameSizes.Contains);

        // Separate, earlier message loop for the pre-handle startup race.
        try { RunPreHandleCase(r); }
        catch (Exception ex) { r.Error = "prehandle:" + ex; }

        var scope = Guid.NewGuid().ToString("N")[..16];
        var eventName = SingleInstanceActivation.EventNameForScope(scope);
        var dataDir = Path.Combine(Path.GetTempPath(), "wlm-activation-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var profiles = new ProfileStore(dataDir);
        profiles.Save(new Profile
        {
            Id = "act-a",
            Name = "Activation A",
            Entries =
            {
                new ProfileEntry
                {
                    Id = "e-act-a", Role = "r", ExePath = @"C:\x\act.exe", ProcessName = "act", ClassName = "ActClass",
                    TitleContains = "act-window", Rect = new SavedRect { X = 100, Y = 100, Width = 400, Height = 300 }
                }
            }
        });
        var settings = new SettingsStore(dataDir);
        settings.Save(new AppSettings { ActiveProfileId = null, MonitoringEnabled = false });
        var hotkeys = new HotkeyStore(dataDir);
        var mock = new MockProvider();
        mock.Add(new WindowInfo
        {
            Identity = new WindowIdentity(new IntPtr(1), 5101, 1000),
            ExePath = @"C:\x\act.exe", ProcessName = "act", ClassName = "ActClass", Title = "act-window",
            CurrentRect = new WindowRect(10, 10, 300, 200), NormalRect = new WindowRect(10, 10, 300, 200),
            ShowState = WindowShowState.Normal, MonitorDevice = @"\\.\DISPLAY1"
        });
        var monitor = new MonitoringService(mock, profiles, settings);
        monitor.InitializeFromSettings();
        var form = new MainForm(profiles, settings, monitor, mock, startInTray: true, hotkeyStore: hotkeys);
        form.UiSmokeSuppressDialogs(true);
        using var listener = new ActivationListener(eventName, () => form.RequestExternalActivation());

        var children = new List<Process>();
        Process? StartClient(bool startInTray, string tag)
        {
            var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--activation-client");
            psi.ArgumentList.Add("--scope"); psi.ArgumentList.Add(scope);
            if (startInTray) psi.ArgumentList.Add("--start-in-tray");
            psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(Path.Combine(outDir, $"{outBase}.client-{tag}.json"));
            var p = Process.Start(psi);
            if (p != null) children.Add(p);
            return p;
        }

        var steps = new List<Step>();
        steps.Add(new Step("identity_and_startup", 300, () =>
        {
            r.Title = form.Text;
            r.TitleOk = form.Text == AppInfo.ProductTitle && form.Text.Contains(AppInfo.DisplayVersion) && form.Text.Contains("WindowDock");
            r.FormIconEmbedded = form.UiSmokeFormIconIsEmbedded;
            r.TrayIconEmbedded = form.UiSmokeTrayIconIsEmbedded;
            r.TraySettingsItemPresent = (form.TraySettingsItem.Text ?? "").Contains("단축키 설정");
            r.StartupHidden = !form.Visible;
        }, () => true, _ => { }));

        Process? dup = null;
        steps.Add(new Step("duplicate_autostart_client", 10000, () => dup = StartClient(true, "dup"),
            () => dup is { HasExited: true },
            _ => { r.DupClientExit = dup is { HasExited: true } ? dup.ExitCode : -1; }));
        steps.Add(new Step("after_duplicate_wait", 1200, () => { }, () => false, _ =>
        {
            r.DupStayedHidden = !form.Visible;
            r.SignalsAfterDup = listener.SignalCount;
        }, timeoutIsSuccess: true));

        Process? normal = null;
        steps.Add(new Step("normal_reopen_client", 10000, () => normal = StartClient(false, "normal"),
            () => normal is { HasExited: true } && form.Visible,
            _ =>
            {
                r.NormalClientExit = normal is { HasExited: true } ? normal.ExitCode : -1;
                r.NormalShownVisible = form.Visible;
                r.NormalShowInTaskbar = form.ShowInTaskbar;
                r.SignalsAfterNormal = listener.SignalCount;
                r.ExternalActivationsAfterNormal = form.UiSmokeExternalActivationCount;
            }));

        steps.Add(new Step("hide_again", 3000, () => form.UiSmokeTriggerHide(), () => !form.Visible, done => r.HiddenAgain = done));

        Process? dup2 = null;
        steps.Add(new Step("duplicate_autostart_again", 10000, () => dup2 = StartClient(true, "dup2"),
            () => dup2 is { HasExited: true },
            _ => { r.DupAgainClientExit = dup2 is { HasExited: true } ? dup2.ExitCode : -1; }));
        steps.Add(new Step("after_duplicate_again_wait", 1200, () => { }, () => false, _ =>
        {
            r.DupAgainStayedHidden = !form.Visible;
            r.SignalsAfterDupAgain = listener.SignalCount;
        }, timeoutIsSuccess: true));

        steps.Add(new Step("no_listener_reports_unavailable", 200, () =>
        {
            var sw = Stopwatch.StartNew();
            var o = SingleInstanceActivation.HandleSecondInstance(false,
                SingleInstanceActivation.EventNameForScope("missing-" + scope), TimeSpan.FromMilliseconds(300));
            r.NoListenerMs = sw.ElapsedMilliseconds;
            r.NoListenerOutcome = o.ToString();
        }, () => true, _ => { }));

        // Tray "단축키 설정…" from the hidden state: shows main + dialog, applies/assigns/enables nothing.
        int q0 = 0, m0 = 0;
        string? active0 = null;
        bool mon0 = false;
        HotkeyConfig? cfg0 = null;
        IReadOnlyDictionary<HotkeyActionId, HotkeyStatus>? st0 = null;
        steps.Add(new Step("tray_settings_open", 5000,
            () =>
            {
                q0 = form.UiSmokeQuickApplyCount;
                m0 = mock.Moves.Count;
                active0 = monitor.ActiveProfile?.Id;
                mon0 = monitor.IsMonitoring;
                cfg0 = form.UiSmokeHotkeyConfig;
                st0 = form.UiSmokeHotkeyStatuses;
                form.TraySettingsItem.PerformClick();
            },
            () => form.UiSmokeOpenHotkeyDialog is { Visible: true },
            done =>
            {
                r.TraySettingsDialogOpened = done;
                r.TraySettingsMainShown = form.Visible;
            }));
        steps.Add(new Step("tray_settings_cancel", 3000,
            () => form.UiSmokeOpenHotkeyDialog?.UiSmokeCancelButton.PerformClick(),
            () => form.UiSmokeOpenHotkeyDialog is null,
            done =>
            {
                var cfg1 = form.UiSmokeHotkeyConfig;
                var st1 = form.UiSmokeHotkeyStatuses;
                bool cfgSame = cfg0 != null && cfg1 != null && cfg1.ContentEquals(cfg0);
                bool statusSame = st0 != null && st1 != null && HotkeyActions.All.All(a => st0[a] == st1[a]);
                bool nothingAssigned = cfg1 != null && cfg1.Bindings.All(b => b.ProfileId == null && !b.Enabled);
                bool noApply = form.UiSmokeQuickApplyCount == q0 && mock.Moves.Count == m0
                               && monitor.ActiveProfile?.Id == active0 && monitor.IsMonitoring == mon0;
                r.TraySettingsNoApply = done && cfgSame && statusSame && nothingAssigned && noApply;
                r.TraySettingsDetail = $"closed={done} cfgSame={cfgSame} statusSame={statusSame} nothingAssigned={nothingAssigned} noApply={noApply} monitoring={monitor.IsMonitoring}";
                Snapshot(form, Path.Combine(outDir, outBase + ".main.png"));
            }));

        steps.Add(new Step("exit", 3000, () => form.TrayExitItem.PerformClick(), () => false, _ => { }, timeoutIsSuccess: true));

        // ---------- runner ----------
        var overallDeadline = DateTime.UtcNow.AddSeconds(90);
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
                    if (!done && !current.TimeoutIsSuccess) r.Timeouts.Add(current.Name);
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

        listener.Dispose();
        r.ListenerReleased = !EventWaitHandle.TryOpenExisting(eventName, out var leftover);
        leftover?.Dispose();

        // Only our own short-lived client processes; they normally have exited already.
        foreach (var p in children)
        {
            try { if (!p.HasExited && !p.WaitForExit(3000)) p.Kill(entireProcessTree: false); } catch { }
            finally { p.Dispose(); }
        }

        r.Ok = r.Error == null && r.Timeouts.Count == 0
               && r.IconLoaded && r.IconLoadError == null && r.IconFramesOk
               && r.FormIconEmbedded && r.TrayIconEmbedded && r.TitleOk && r.TraySettingsItemPresent
               && r.StartupHidden
               && r.DupClientExit == 10 && r.DupStayedHidden && r.SignalsAfterDup == 0
               && r.NormalClientExit == 0 && r.NormalShownVisible && r.NormalShowInTaskbar
               && r.SignalsAfterNormal == 1 && r.ExternalActivationsAfterNormal == 1
               && r.HiddenAgain
               && r.DupAgainClientExit == 10 && r.DupAgainStayedHidden && r.SignalsAfterDupAgain == 1
               && r.NoListenerOutcome == nameof(SecondInstanceOutcome.ActivationUnavailable) && r.NoListenerMs < 2000
               && r.TraySettingsDialogOpened && r.TraySettingsMainShown && r.TraySettingsNoApply
               && r.ApplicationRunReturned && r.ListenerReleased
               && r.PreHandleOk;
        CliCommon.WriteJson(outPath, r);
        return r.Ok ? 0 : 1;
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
        private readonly List<WindowInfo> _windows = new();
        public List<(WindowIdentity id, WindowRect target, bool max)> Moves { get; } = new();
        private readonly List<MonitorInfo> _monitors = new()
        {
            new MonitorInfo(@"\\.\DISPLAY1", new WindowRect(0, 0, 1920, 1080), new WindowRect(0, 0, 1920, 1040), true)
        };

        public void Add(WindowInfo w) => _windows.Add(w);

        public EnumerationResult Enumerate() => new()
        {
            Windows = _windows.ToList(),
            Excluded = Array.Empty<ExcludedWindow>(),
            Monitors = _monitors.ToList()
        };

        public bool IsAlive(WindowIdentity id) => _windows.Any(w => w.Identity.Equals(id));
        public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => _windows.Select(w => w.Identity).ToList();
        public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata() => _windows.ToList();

        public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string exeKey, string? className = null)
        {
            Moves.Add((id, target, maximize));
            return new MoveResult(MoveOutcome.Success, target, null);
        }
    }
}
