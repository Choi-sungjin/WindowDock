using System.Diagnostics;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

public static class ValidateCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--validate-profile requires <id>"); return 2; }
        var id = args[1];
        var dataDir = CliCommon.GetArg(args, "--data-dir") ?? DataPaths.DefaultDataDir();
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }

        var store = new ProfileStore(dataDir);
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
        var en = new WindowEnumerator();
        var snap = en.Enumerate(Process.GetCurrentProcess().Id);
        var matches = LayoutEngine.MatchProfile(profile, snap.Windows);

        CliCommon.WriteJson(outPath, new
        {
            ok = true,
            schemaVersion = 1,
            profileId = profile.Id,
            profileName = profile.Name,
            matchedCount = matches.Count(m => m.Outcome == MatchOutcome.Matched),
            noMatchCount = matches.Count(m => m.Outcome == MatchOutcome.NoMatch),
            ambiguousCount = matches.Count(m => m.Outcome == MatchOutcome.Ambiguous),
            entries = matches.Select(m => new
            {
                entryId = m.Entry.Id,
                role = m.Entry.Role,
                exe = m.Entry.ExePath,
                className = m.Entry.ClassName,
                titleContains = m.Entry.TitleContains,
                outcome = m.Outcome.ToString(),
                note = m.Note,
                target = new { m.Entry.Rect.X, m.Entry.Rect.Y, w = m.Entry.Rect.Width, h = m.Entry.Rect.Height },
                clamped = ToObj(LayoutEngine.ClampToWorkArea(m.Entry.Rect.ToWindowRect(), snap.Monitors, out _)),
                matchedWindow = m.Window == null ? null : new
                {
                    pid = m.Window.Identity.Pid,
                    title = m.Window.Title,
                    current = new { m.Window.CurrentRect.X, m.Window.CurrentRect.Y, w = m.Window.CurrentRect.Width, h = m.Window.CurrentRect.Height }
                },
                candidateCount = m.Candidates.Count
            }).ToList()
        });
        return 0;
    }

    private static object ToObj(Model.WindowRect r) => new { r.X, r.Y, w = r.Width, h = r.Height };
}
