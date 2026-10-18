using System.Diagnostics;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

public static class ApplyCommand
{
    /// <summary>
    /// CLI apply: sets the given profile as the ACTIVE profile, applies it, and persists the
    /// active+monitoring-enabled state so the next GUI startup resumes monitoring. The CLI
    /// process itself exits after the one-shot apply — ongoing monitoring is owned by the GUI.
    /// Malformed profile JSON is surfaced as structured error output with a nonzero exit; the
    /// original file is preserved by ProfileStore (never deleted or overwritten).
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--apply-profile requires <id>"); return 2; }
        var id = args[1];
        var dataDir = CliCommon.GetArg(args, "--data-dir") ?? DataPaths.DefaultDataDir();
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }

        var store = new ProfileStore(dataDir);
        var settings = new SettingsStore(dataDir);
        Model.Profile? profile;
        try
        {
            profile = store.Load(id);
        }
        catch (Exception ex)
        {
            CliCommon.WriteJson(outPath, new
            {
                ok = false,
                error = "profile_malformed",
                detail = ex.Message,
                exception = ex.GetType().Name,
                profileId = id
            });
            return 4;
        }
        if (profile == null)
        {
            CliCommon.WriteJson(outPath, new { ok = false, error = "profile_not_found", profileId = id });
            return 3;
        }
        var provider = new ProductionWindowProvider(Process.GetCurrentProcess().Id);
        var monitor = new MonitoringService(provider, store, settings);
        // Set active FIRST so ApplyActiveProfile enables + persists monitoring and the GUI picks
        // this up on next startup. SetActiveProfile also persists activeProfileId.
        monitor.SetActiveProfile(profile);
        var persistAfterSetActive = monitor.LastPersistError;
        var reports = monitor.ApplyActiveProfile();
        var persistAfterApply = monitor.LastPersistError;

        // Truthfully verify FINAL persisted state against the intended one. A later success does
        // NOT retroactively "undo" an earlier persistence failure — we re-read the file and
        // compare. This prevents the CLI from reporting ok:true while the lock stopped our writes.
        var persisted = new SettingsStore(dataDir).Load();
        bool persistOk = string.Equals(persisted.ActiveProfileId, profile.Id, StringComparison.Ordinal)
                         && persisted.MonitoringEnabled == true
                         && string.IsNullOrEmpty(persistAfterSetActive)
                         && string.IsNullOrEmpty(persistAfterApply);

        var reportList = reports.Select(r => new
        {
            entryId = r.EntryId,
            role = r.EntryRole,
            result = r.Result.ToString(),
            windowPid = r.WindowId?.Pid,
            note = r.Note,
            target = new { r.TargetRect.X, r.TargetRect.Y, w = r.TargetRect.Width, h = r.TargetRect.Height },
            adjusted = r.Adjusted
        }).ToList();
        int movedCount = reports.Count(r => r.Result == PlaceResult.Moved);
        int skippedCount = reports.Count(r => r.Result != PlaceResult.Moved);

        if (!persistOk)
        {
            // Honest structured failure. We DO NOT roll back any native move that already happened;
            // we report those moves truthfully alongside the persistence failure.
            CliCommon.WriteJson(outPath, new
            {
                ok = false,
                error = "settings_persistence_failed",
                detail = persistAfterApply ?? persistAfterSetActive ?? "activeProfileId or monitoringEnabled did not reach disk",
                profileId = profile.Id,
                profileName = profile.Name,
                movedCount,
                skippedCount,
                activeProfileIdPersisted = persisted.ActiveProfileId,
                monitoringEnabledPersisted = persisted.MonitoringEnabled,
                persistErrorAfterSetActive = persistAfterSetActive,
                persistErrorAfterApply = persistAfterApply,
                lifetime = "cli_persistence_failed_native_moves_not_rolled_back",
                reports = reportList
            });
            return 5;
        }

        CliCommon.WriteJson(outPath, new
        {
            ok = true,
            profileId = profile.Id,
            profileName = profile.Name,
            movedCount,
            skippedCount,
            monitoringEnabledPersisted = persisted.MonitoringEnabled,
            activeProfileIdPersisted = persisted.ActiveProfileId,
            lifetime = "cli_exits_after_one_shot_apply_gui_owns_ongoing_monitoring",
            reports = reportList
        });
        return 0;
    }
}
