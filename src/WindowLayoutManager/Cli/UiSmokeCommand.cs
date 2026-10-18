using System.Diagnostics;
using WindowLayoutManager.App;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

/// <summary>
/// UI smoke: spawns 3 owned fixtures, pre-creates a 3-entry profile pointing at them, launches the
/// actual MainForm, exercises the real Apply button + tray submenu + the real Update button via an
/// injected confirmation handler for Cancel/Yes/SaveFailure. Role C is closed before the Update
/// phase so the "closed role preserved" invariant can be verified. Save failure is driven by a real
/// filesystem lock on the target profile JSON (not just an injected throw). All mutations are
/// strictly scoped to the fixture PIDs via a RecordingProvider and re-checked against the owned
/// child-process list before each harness mutation.
/// </summary>
public static class UiSmokeCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--ui-smoke requires <png>"); return 2; }
        var pngPath = args[1];
        var dataDir = CliCommon.GetArg(args, "--data-dir") ?? Path.Combine(Path.GetTempPath(), "wlm-ui-smoke-" + Guid.NewGuid().ToString("N"));
        var reportPath = CliCommon.GetArg(args, "--output") ?? Path.ChangeExtension(pngPath, ".json")!;
        Directory.CreateDirectory(dataDir);
        var pngDir = Path.GetDirectoryName(pngPath);
        if (!string.IsNullOrEmpty(pngDir)) Directory.CreateDirectory(pngDir);

        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath))
        {
            CliCommon.WriteJson(reportPath, new { ok = false, error = "no_exe_path" });
            return 3;
        }

        var allowedPids = new HashSet<int>();
        var providerMoves = new List<int>();
        var violations = new List<int>();
        var children = new List<Process>();
        // Win32 FILETIME-based start-tick map, captured from WindowEnumerator identity (same epoch
        // as the matcher/mover). Re-checked at the HWND level before every harness mutation.
        var ownedStartTicks = new Dictionary<int, long>();
        // Marker → captured owned pid, so each PerturbFixture/close call verifies the actual
        // target identity (not merely "some owned fixture").
        var markerToPid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? setupError = null;
        Profile? seededProfile = null;

        var markers = new[] { "wlmUiX7f2", "wlmUiY7f2", "wlmUiZ7f2" };
        var startRects = new (int x, int y, int w, int h)[]
        {
            (320, 220, 400, 260),
            (820, 280, 420, 280),
            (520, 500, 380, 240)
        };
        try
        {
            for (int i = 0; i < markers.Length; i++)
            {
                var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
                psi.ArgumentList.Add("--test-window");
                psi.ArgumentList.Add(markers[i]);
                psi.ArgumentList.Add("--x"); psi.ArgumentList.Add(startRects[i].x.ToString());
                psi.ArgumentList.Add("--y"); psi.ArgumentList.Add(startRects[i].y.ToString());
                psi.ArgumentList.Add("--w"); psi.ArgumentList.Add(startRects[i].w.ToString());
                psi.ArgumentList.Add("--h"); psi.ArgumentList.Add(startRects[i].h.ToString());
                psi.ArgumentList.Add("--lifetime-ms"); psi.ArgumentList.Add("120000");
                var p = Process.Start(psi) ?? throw new InvalidOperationException("spawn_failed");
                children.Add(p);
                allowedPids.Add(p.Id);
                // Win32 FILETIME is populated after window surfaces (loop below).
            }

            var enumRaw = new WindowEnumerator();
            Dictionary<string, WindowInfo> byMarker = new();
            var waitOk = Wait(() =>
            {
                var res = enumRaw.Enumerate(Process.GetCurrentProcess().Id);
                byMarker.Clear();
                foreach (var m in markers)
                {
                    var w = res.Windows.FirstOrDefault(x => x.ClassName == TestWindowCommand.FixtureClassName
                                                            && x.Title.Contains(m, StringComparison.OrdinalIgnoreCase));
                    if (w != null) byMarker[m] = w;
                }
                return byMarker.Count == markers.Length;
            }, TimeSpan.FromSeconds(10));
            if (!waitOk) throw new InvalidOperationException($"fixtures_not_visible count={byMarker.Count}");
            foreach (var kv in byMarker)
            {
                ownedStartTicks[kv.Value.Identity.Pid] = kv.Value.Identity.ProcessStartTicks;
                markerToPid[kv.Key] = kv.Value.Identity.Pid;
            }

            seededProfile = new Profile
            {
                Id = ProfileStore.GenerateId(),
                Name = "UI Smoke Fixture"
            };
            var targetRects = new (int x, int y, int w, int h)[]
            {
                (160, 160, 440, 280),
                (640, 180, 460, 300),
                (420, 440, 380, 240)
            };
            for (int i = 0; i < markers.Length; i++)
            {
                var w = byMarker[markers[i]];
                seededProfile.Entries.Add(new ProfileEntry
                {
                    Id = ProfileStore.GenerateId(),
                    Role = "ui-" + markers[i],
                    ExePath = w.ExePath,
                    ProcessName = w.ProcessName,
                    ClassName = w.ClassName,
                    TitleContains = markers[i],
                    Rect = new SavedRect { X = targetRects[i].x, Y = targetRects[i].y, Width = targetRects[i].w, Height = targetRects[i].h }
                });
            }
            new ProfileStore(dataDir).Save(seededProfile);
        }
        catch (Exception ex)
        {
            setupError = ex.ToString();
            CleanupChildren(children, ownedStartTicks);
            CliCommon.WriteJson(reportPath, new { ok = false, setupError });
            return 4;
        }

        var profiles = new ProfileStore(dataDir);
        var settings = new SettingsStore(dataDir);
        var baseProvider = new ProductionWindowProvider(Process.GetCurrentProcess().Id);
        var recording = new RecordingProvider(baseProvider, allowedPids, violations, providerMoves);
        var monitor = new MonitoringService(recording, profiles, settings);
        monitor.InitializeFromSettings();

        bool defaultCaptured = false, minCaptured = false;
        bool initialMonitoringOffOk = false;
        bool trayStartTogglesOnOk = false;
        bool trayPauseTogglesOffOk = false;
        bool hideHidesOk = false, trayOpenShowsOk = false;
        bool exitRequested = false;
        bool applyMovedAll = false;
        bool applyEnabledMonitoring = false;
        bool trayProfileApplyWorked = false;
        bool updateCancelPreserved = false;
        bool updateYesPersistedAndRefreshed = false;
        bool updateSaveFailurePreserved = false;
        bool entryEditDialogBoundsOk = false;
        bool closedRoleCPreservedThroughUpdate = false;
        string? closedRoleCId = null;
        SavedRect? closedRoleCRectBeforeUpdates = null;
        string? error = null;
        int lastPhase = -1;

        var form = new MainForm(profiles, settings, monitor, recording);
        form.UiSmokeSuppressDialogs(true);
        var fireTimer = new System.Windows.Forms.Timer { Interval = 500 };
        int phase = 0;
        bool inTick = false;
        const int totalPhases = 15;

        // Strict HWND-tied identity check: resolves hwnd → live pid + Win32 FILETIME + class, and
        // requires the resolved pid to EQUAL the caller's expected pid as well as be in the owned
        // snapshot. PID-only ownership is insufficient — the HWND could have been destroyed and
        // the handle reused for another window of the same PID; and the enumerated candidate must
        // match the specific target the caller intended, not merely "any owned fixture".
        bool VerifyOwnedHwnd(IntPtr hwnd, int expectedPid)
        {
            if (hwnd == IntPtr.Zero) return false;
            var id = WindowEnumerator.ReadIdentity(hwnd);
            if (id is null) return false;
            if (id.Pid != expectedPid) return false;
            if (!ownedStartTicks.TryGetValue(id.Pid, out var expectedStart)) return false;
            if (id.ProcessStartTicks != expectedStart) return false;
            var cls = WindowEnumerator.ReadClassName(hwnd);
            return cls == TestWindowCommand.FixtureClassName;
        }

        void PerturbFixture(string marker, int x, int y, int w, int h)
        {
            if (!markerToPid.TryGetValue(marker, out var expectedPid))
                throw new InvalidOperationException($"perturb_marker_not_registered:{marker}");
            var fx = new WindowEnumerator().Enumerate(Process.GetCurrentProcess().Id).Windows
                .FirstOrDefault(ww => ww.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)
                                      && ww.ClassName == TestWindowCommand.FixtureClassName
                                      && ww.Identity.Pid == expectedPid);
            if (fx == null) throw new InvalidOperationException($"perturb_target_missing:{marker}");
            if (!VerifyOwnedHwnd(fx.Identity.Hwnd, expectedPid))
                throw new InvalidOperationException($"perturb_target_not_owned_fixture hwnd={fx.Identity.Hwnd.ToInt64()} expectedPid={expectedPid} actualPid={fx.Identity.Pid}");
            NativeMethods.SetWindowPos(fx.Identity.Hwnd, IntPtr.Zero, x, y, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
            Thread.Sleep(220);
        }

        fireTimer.Tick += (s, _) =>
        {
            if (inTick) return;
            inTick = true;
            fireTimer.Stop();
            lastPhase = phase;
            try
            {
                switch (phase)
                {
                    case 0:
                        initialMonitoringOffOk = !monitor.IsMonitoring && monitor.ActiveProfile == null;
                        using (var bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        defaultCaptured = File.Exists(pngPath);
                        form.Size = form.MinimumSize;
                        break;
                    case 1:
                        Application.DoEvents();
                        var minPath = Path.Combine(Path.GetDirectoryName(pngPath) ?? ".",
                            Path.GetFileNameWithoutExtension(pngPath) + ".min.png");
                        using (var bmp2 = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp2, new Rectangle(0, 0, form.Width, form.Height));
                            bmp2.Save(minPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        minCaptured = File.Exists(minPath);
                        break;
                    case 2:
                    {
                        // EntryEditDialog default/minimum bounds + strict four-field verification.
                        //  (1) Exactly four geometry input fields (X, Y, Width, Height) exist via
                        //      the test hook UiSmokeRectFields.
                        //  (2) Each is Visible AND Enabled.
                        //  (3) Each field's screen rectangle is fully inside the dialog's screen
                        //      client rectangle at the default size AND at the minimum size.
                        var seededReloaded = new ProfileStore(dataDir).Load(seededProfile!.Id)!;
                        var dlgEntry = seededReloaded.Entries[0];
                        using var dlg = new EntryEditDialog(dlgEntry);
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(80, 80);
                        dlg.Show();
                        Application.DoEvents();
                        Thread.Sleep(120);

                        bool CheckFields(EntryEditDialog d)
                        {
                            var fields = d.UiSmokeRectFields;
                            if (fields == null || fields.Count != 4) return false;
                            var dClient = d.RectangleToScreen(d.ClientRectangle);
                            foreach (var f in fields)
                            {
                                if (!f.Visible || !f.Enabled) return false;
                                if (f.Width <= 0 || f.Height <= 0) return false;
                                var fScreen = f.RectangleToScreen(f.ClientRectangle);
                                if (!dClient.Contains(fScreen)) return false;
                            }
                            return true;
                        }

                        bool defaultOk = CheckFields(dlg);
                        var editPngDefault = Path.Combine(Path.GetDirectoryName(pngPath) ?? ".",
                            Path.GetFileNameWithoutExtension(pngPath) + ".edit-default.png");
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(editPngDefault, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Size = dlg.MinimumSize;
                        Application.DoEvents();
                        Thread.Sleep(120);
                        bool minOk = CheckFields(dlg);
                        var editPngMin = Path.Combine(Path.GetDirectoryName(pngPath) ?? ".",
                            Path.GetFileNameWithoutExtension(pngPath) + ".edit-min.png");
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(editPngMin, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                        entryEditDialogBoundsOk = defaultOk && minOk
                            && File.Exists(editPngDefault) && File.Exists(editPngMin);
                        break;
                    }
                    case 3:
                        form.UiSmokeForceRefreshTrayMenu();
                        form.TrayPauseItem.PerformClick();
                        break;
                    case 4:
                        trayStartTogglesOnOk = monitor.IsMonitoring;
                        form.TrayPauseItem.PerformClick();
                        break;
                    case 5:
                        trayPauseTogglesOffOk = !monitor.IsMonitoring;
                        form.UiSmokeSelectProfileById(seededProfile!.Id);
                        form.UiSmokePerformApplyClick();
                        break;
                    case 6:
                    {
                        var enumRaw = new WindowEnumerator();
                        var snap = enumRaw.Enumerate(Process.GetCurrentProcess().Id);
                        int ok = 0;
                        foreach (var marker in markers)
                        {
                            var w = snap.Windows.FirstOrDefault(x => x.Title.Contains(marker, StringComparison.OrdinalIgnoreCase));
                            if (w == null) continue;
                            var want = seededProfile!.Entries.First(e => e.TitleContains == marker).Rect;
                            if (Math.Abs(w.CurrentRect.X - want.X) <= 25 && Math.Abs(w.CurrentRect.Y - want.Y) <= 25
                                && Math.Abs(w.CurrentRect.Width - want.Width) <= 40 && Math.Abs(w.CurrentRect.Height - want.Height) <= 40)
                                ok++;
                        }
                        applyMovedAll = ok == markers.Length;
                        applyEnabledMonitoring = monitor.IsMonitoring;
                        break;
                    }
                    case 7:
                    {
                        var item = FindTrayProfileItem(form, seededProfile!.Id);
                        if (item == null) throw new InvalidOperationException("tray_submenu_item_missing");
                        item.PerformClick();
                        trayProfileApplyWorked = monitor.IsMonitoring && monitor.ActiveProfile?.Id == seededProfile.Id;
                        break;
                    }
                    case 8:
                    {
                        // Close role C to prove the Update workflow preserves closed-role entries.
                        var live = form.UiSmokeSelectedProfile()!;
                        var roleC = live.Entries.First(e => e.TitleContains == markers[2]);
                        closedRoleCId = roleC.Id;
                        closedRoleCRectBeforeUpdates = CopyRect(roleC.Rect);
                        if (!markerToPid.TryGetValue(markers[2], out var expectedCpid))
                            throw new InvalidOperationException($"close_marker_not_registered:{markers[2]}");
                        var cProcCandidates = new WindowEnumerator().Enumerate(Process.GetCurrentProcess().Id).Windows
                            .Where(w => w.ClassName == TestWindowCommand.FixtureClassName
                                        && w.Title.Contains(markers[2], StringComparison.OrdinalIgnoreCase)
                                        && w.Identity.Pid == expectedCpid)
                            .Where(w => VerifyOwnedHwnd(w.Identity.Hwnd, expectedCpid))
                            .ToList();
                        Process? cProc = null;
                        foreach (var w in cProcCandidates)
                        {
                            // Re-verify immediately before PostMessage (HWND could have been
                            // destroyed and the slot reused between filter time and here).
                            if (!VerifyOwnedHwnd(w.Identity.Hwnd, expectedCpid)) continue;
                            PostMessage(w.Identity.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            cProc = children.FirstOrDefault(p => p.Id == w.Identity.Pid);
                        }
                        if (cProc != null)
                        {
                            cProc.WaitForExit(3000);
                            allowedPids.Remove(cProc.Id);
                            ownedStartTicks.Remove(cProc.Id);
                            markerToPid.Remove(markers[2]);
                        }
                        Wait(() => !new WindowEnumerator().Enumerate(Process.GetCurrentProcess().Id).Windows
                            .Any(x => x.Title.Contains(markers[2], StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(5));

                        // CANCEL case: perturb A, click the REAL Update button with injected 'No', verify preservation.
                        PerturbFixture(markers[0], 50, 50, 300, 200);
                        var liveBefore = form.UiSmokeSelectedProfile()!;
                        var roleA = liveBefore.Entries.First(e => e.TitleContains == markers[0]);
                        var beforeRectA = CopyRect(roleA.Rect);
                        var beforeFilterA = roleA.TitleContains;
                        var beforeRoleA = roleA.Role;
                        var beforeIdA = roleA.Id;
                        form.UpdateConfirmHandler = _ => false;
                        form.UiSmokePerformUpdateClick();
                        var outcomeCancel = form.LastUpdateOutcome;
                        var liveAfter = form.UiSmokeSelectedProfile()!;
                        var reloaded = new ProfileStore(dataDir).Load(liveAfter.Id)!;
                        var roleAafter = liveAfter.Entries.First(e => e.Id == beforeIdA);
                        var roleAreloaded = reloaded.Entries.First(e => e.Id == beforeIdA);
                        var roleCafter = liveAfter.Entries.First(e => e.Id == closedRoleCId);
                        var roleCreloaded = reloaded.Entries.First(e => e.Id == closedRoleCId);
                        updateCancelPreserved = outcomeCancel == MainForm.UpdateOutcome.Cancelled
                            && SameRect(roleAafter.Rect, beforeRectA)
                            && SameRect(roleAreloaded.Rect, beforeRectA)
                            && roleAafter.TitleContains == beforeFilterA
                            && roleAafter.Role == beforeRoleA
                            && SameRect(roleCafter.Rect, closedRoleCRectBeforeUpdates!)
                            && SameRect(roleCreloaded.Rect, closedRoleCRectBeforeUpdates!)
                            && roleCafter.Id == closedRoleCId;
                        break;
                    }
                    case 9:
                    {
                        // YES case: perturb A to a DIFFERENT target, click the REAL Update button.
                        PerturbFixture(markers[0], 170, 230, 360, 240);
                        var liveBefore = form.UiSmokeSelectedProfile()!;
                        var roleIdA = liveBefore.Entries.First(e => e.TitleContains == markers[0]).Id;
                        form.UpdateConfirmHandler = _ => true;
                        form.UiSmokePerformUpdateClick();
                        var outcomeYes = form.LastUpdateOutcome;
                        var liveAfter = form.UiSmokeSelectedProfile()!;
                        var reloaded = new ProfileStore(dataDir).Load(liveAfter.Id)!;
                        var roleAafter = liveAfter.Entries.First(e => e.Id == roleIdA);
                        var roleAreloaded = reloaded.Entries.First(e => e.Id == roleIdA);
                        var roleCafter = liveAfter.Entries.First(e => e.Id == closedRoleCId);
                        var roleCreloaded = reloaded.Entries.First(e => e.Id == closedRoleCId);
                        updateYesPersistedAndRefreshed = outcomeYes == MainForm.UpdateOutcome.Applied
                            && Math.Abs(roleAafter.Rect.X - 170) <= 30
                            && Math.Abs(roleAafter.Rect.Y - 230) <= 30
                            && Math.Abs(roleAreloaded.Rect.X - 170) <= 30
                            && Math.Abs(roleAreloaded.Rect.Y - 230) <= 30
                            && SameRect(roleCafter.Rect, closedRoleCRectBeforeUpdates!)
                            && SameRect(roleCreloaded.Rect, closedRoleCRectBeforeUpdates!)
                            && roleCafter.Id == closedRoleCId;
                        break;
                    }
                    case 10:
                    {
                        // SAVE-FAILURE case: perturb A to yet ANOTHER target so the proposed geometry
                        // genuinely differs from current. Lock the profile JSON file at the OS level
                        // (FileShare.None) so File.Replace fails inside ProfileStore.Save — a real IO
                        // failure, not an injected throw. Verify live+disk preservation.
                        PerturbFixture(markers[0], 90, 310, 420, 300);
                        var liveBefore = form.UiSmokeSelectedProfile()!;
                        var roleIdA = liveBefore.Entries.First(e => e.TitleContains == markers[0]).Id;
                        var beforeRectA = CopyRect(liveBefore.Entries.First(e => e.Id == roleIdA).Rect);
                        var beforeRectC = CopyRect(liveBefore.Entries.First(e => e.Id == closedRoleCId).Rect);
                        var profilePath = Path.Combine(dataDir, "profiles", liveBefore.Id + ".json");
                        bool fsLocked = false;
                        FileStream? lockHandle = null;
                        try
                        {
                            lockHandle = new FileStream(profilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            fsLocked = true;
                            form.UpdateConfirmHandler = _ => true;
                            form.UiSmokePerformUpdateClick();
                        }
                        finally
                        {
                            lockHandle?.Dispose();
                        }
                        var outcomeFail = form.LastUpdateOutcome;
                        var liveAfter = form.UiSmokeSelectedProfile()!;
                        var reloaded = new ProfileStore(dataDir).Load(liveAfter.Id)!;
                        var roleAafter = liveAfter.Entries.First(e => e.Id == roleIdA);
                        var roleAreloaded = reloaded.Entries.First(e => e.Id == roleIdA);
                        var roleCafter = liveAfter.Entries.First(e => e.Id == closedRoleCId);
                        var roleCreloaded = reloaded.Entries.First(e => e.Id == closedRoleCId);
                        updateSaveFailurePreserved = outcomeFail == MainForm.UpdateOutcome.SaveFailed
                            && fsLocked
                            && SameRect(roleAafter.Rect, beforeRectA)
                            && SameRect(roleAreloaded.Rect, beforeRectA)
                            && SameRect(roleCafter.Rect, beforeRectC)
                            && SameRect(roleCreloaded.Rect, beforeRectC);
                        closedRoleCPreservedThroughUpdate = SameRect(roleCreloaded.Rect, closedRoleCRectBeforeUpdates!)
                            && roleCafter.Id == closedRoleCId;
                        break;
                    }
                    case 11:
                        form.UiSmokeTriggerHide();
                        break;
                    case 12:
                        hideHidesOk = !form.Visible;
                        form.TrayOpenItem.PerformClick();
                        break;
                    case 13:
                        trayOpenShowsOk = form.Visible;
                        break;
                    case 14:
                        exitRequested = true;
                        fireTimer.Stop();
                        form.TrayExitItem.PerformClick();
                        break;
                }
                phase++;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                fireTimer.Stop();
                form.UiSmokeTriggerExit();
                inTick = false;
                return;
            }
            inTick = false;
            if (phase < totalPhases) fireTimer.Start();
        };
        form.Shown += (_, _) => fireTimer.Start();
        Application.Run(form);
        CleanupChildren(children, ownedStartTicks);

        var ok = defaultCaptured && minCaptured && initialMonitoringOffOk && entryEditDialogBoundsOk &&
                 trayStartTogglesOnOk && trayPauseTogglesOffOk &&
                 applyMovedAll && applyEnabledMonitoring && trayProfileApplyWorked &&
                 updateCancelPreserved && updateYesPersistedAndRefreshed && updateSaveFailurePreserved &&
                 closedRoleCPreservedThroughUpdate &&
                 hideHidesOk && trayOpenShowsOk && exitRequested && error == null &&
                 violations.Count == 0;
        CliCommon.WriteJson(reportPath, new
        {
            ok,
            defaultCaptured,
            minCaptured,
            initialMonitoringOffOk,
            entryEditDialogBoundsOk,
            trayStartTogglesOnOk,
            trayPauseTogglesOffOk,
            applyMovedAll,
            applyEnabledMonitoring,
            trayProfileApplyWorked,
            updateCancelPreserved,
            updateYesPersistedAndRefreshed,
            updateSaveFailurePreserved,
            closedRoleCPreservedThroughUpdate,
            hideHidesOk,
            trayOpenShowsOk,
            exitRequested,
            lastPhase,
            error,
            setupError,
            pngPath,
            dataDir,
            providerMoveTargets = providerMoves.Distinct().ToArray(),
            allowedPids = allowedPids.ToArray(),
            violations = violations.ToArray()
        });
        return ok ? 0 : 1;
    }

    private static ToolStripMenuItem? FindTrayProfileItem(MainForm form, string profileId)
    {
        foreach (ToolStripItem it in form.TrayApplyMenu.DropDownItems)
        {
            if (it is ToolStripMenuItem mi && mi.Tag is Profile p && p.Id == profileId) return mi;
        }
        return null;
    }

    private static SavedRect CopyRect(SavedRect r) => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
    private static bool SameRect(SavedRect a, SavedRect b) => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    private static bool Wait(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }

    /// <summary>
    /// Cleanup only our spawned fixtures. Before every PostMessage the live HWND must resolve to
    /// the expected PID AND the exact Win32 FILETIME we captured AND the fixture class. Before any
    /// Kill, the pid must still map to a process whose native creation FILETIME equals what we
    /// captured — read via OpenProcess+GetProcessTimes, NOT .NET DateTime.Ticks. If identity
    /// cannot be proved we skip the mutation and rely on the fixture's own --lifetime-ms watchdog.
    /// </summary>
    private static void CleanupChildren(List<Process> children, IReadOnlyDictionary<int, long> ownedStartTicks)
    {
        bool OwnedHwnd(IntPtr hwnd, int expectedPid)
        {
            if (hwnd == IntPtr.Zero) return false;
            var id = WindowEnumerator.ReadIdentity(hwnd);
            if (id is null) return false;
            if (id.Pid != expectedPid) return false;
            if (!ownedStartTicks.TryGetValue(id.Pid, out var expectedStart)) return false;
            if (id.ProcessStartTicks != expectedStart) return false;
            return WindowEnumerator.ReadClassName(hwnd) == TestWindowCommand.FixtureClassName;
        }
        bool OwnedProcess(int pid)
        {
            if (!ownedStartTicks.TryGetValue(pid, out var expectedStart)) return false;
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (!NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _)) return false;
                return created == expectedStart;
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        foreach (var p in children)
        {
            try
            {
                if (!p.HasExited)
                {
                    var en = new WindowEnumerator();
                    var snap = en.Enumerate(Process.GetCurrentProcess().Id);
                    foreach (var w in snap.Windows.Where(w => w.Identity.Pid == p.Id
                                                                && w.ClassName == TestWindowCommand.FixtureClassName))
                    {
                        if (OwnedHwnd(w.Identity.Hwnd, p.Id))
                        {
                            PostMessage(w.Identity.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    if (!p.WaitForExit(1500) && OwnedProcess(p.Id))
                    {
                        try { p.Kill(entireProcessTree: false); } catch { }
                    }
                    // Else: identity cannot be proved — skip termination; fixture --lifetime-ms watchdog.
                }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
    }

    private sealed class RecordingProvider : IWindowProvider
    {
        private readonly IWindowProvider _inner;
        private readonly HashSet<int> _allowed;
        private readonly List<int> _violations;
        private readonly List<int> _all;
        public RecordingProvider(IWindowProvider inner, HashSet<int> allowed, List<int> violations, List<int> all)
        {
            _inner = inner; _allowed = allowed; _violations = violations; _all = all;
        }
        public EnumerationResult Enumerate() => _inner.Enumerate();
        public bool IsAlive(WindowIdentity id) => _inner.IsAlive(id);
        public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities() => _inner.EnumerateAnyTopLevelIdentities();
        public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata() => _inner.EnumerateAnyTopLevelWithMetadata();
        public MoveResult Move(WindowIdentity id, Model.WindowRect target, bool maximize, string exeKey, string? className = null)
        {
            _all.Add(id.Pid);
            if (!_allowed.Contains(id.Pid))
            {
                _violations.Add(id.Pid);
                return new MoveResult(MoveOutcome.Rejected, default, "pid_not_allowed_by_ui_smoke");
            }
            return _inner.Move(id, target, maximize, exeKey, className);
        }
    }

    private const uint WM_CLOSE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
