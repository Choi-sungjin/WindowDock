using System.Diagnostics;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Cli;

public static class SaveCurrentCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--save-current requires <name>"); return 2; }
        var name = args[1];
        var dataDir = CliCommon.GetArg(args, "--data-dir") ?? DataPaths.DefaultDataDir();
        var outPath = CliCommon.GetArg(args, "--output");
        if (string.IsNullOrEmpty(outPath)) { Console.Error.WriteLine("--output required"); return 2; }

        var en = new WindowEnumerator();
        var res = en.Enumerate(Process.GetCurrentProcess().Id);

        var profile = new Profile
        {
            Id = ProfileStore.GenerateId(),
            Name = name,
            Entries = new List<ProfileEntry>()
        };
        var groups = res.Windows.GroupBy(w => (w.ExeKey, w.ClassName)).ToDictionary(g => g.Key, g => g.ToList());
        int roleIdx = 0;
        foreach (var w in res.Windows)
        {
            roleIdx++;
            var group = groups[(w.ExeKey, w.ClassName)];
            string? suggested = null;
            if (group.Count > 1)
            {
                var siblings = group.Where(x => x.Identity != w.Identity).Select(x => x.Title);
                suggested = LayoutEngine.SuggestTitleContainsFor(w.Title, siblings);
            }
            profile.Entries.Add(new ProfileEntry
            {
                Id = ProfileStore.GenerateId(),
                Role = group.Count == 1 ? w.ProcessName : $"{w.ProcessName}-{roleIdx}",
                ExePath = w.ExePath,
                ProcessName = w.ProcessName,
                ClassName = w.ClassName,
                TitleContains = suggested,
                // Store NormalRect for both minimized AND maximized (preserves restore geometry).
                // Only Normal state uses the live CurrentRect.
                Rect = SavedRect.From(w.ShowState == WindowShowState.Normal ? w.CurrentRect : w.NormalRect),
                Maximized = w.ShowState == WindowShowState.Maximized
            });
        }

        var store = new ProfileStore(dataDir);
        try
        {
            store.Save(profile);
        }
        catch (Exception ex)
        {
            CliCommon.WriteJson(outPath, new { ok = false, error = ex.Message });
            return 3;
        }

        CliCommon.WriteJson(outPath, new
        {
            ok = true,
            profileId = profile.Id,
            profileName = profile.Name,
            entryCount = profile.Entries.Count,
            entries = profile.Entries.Select(e => new
            {
                e.Id,
                e.Role,
                e.ProcessName,
                e.ClassName,
                e.TitleContains,
                rect = new { e.Rect.X, e.Rect.Y, w = e.Rect.Width, h = e.Rect.Height },
                e.Maximized
            }).ToList()
        });
        return 0;
    }
}
