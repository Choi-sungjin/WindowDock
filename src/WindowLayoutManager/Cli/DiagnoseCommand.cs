using System.Diagnostics;
using WindowLayoutManager.Native;

namespace WindowLayoutManager.Cli;

public static class DiagnoseCommand
{
    public static int Run(string[] args)
    {
        var outPath = args.Length >= 2 ? args[1] : null;
        if (string.IsNullOrEmpty(outPath))
        {
            Console.Error.WriteLine("--diagnose requires an output path");
            return 2;
        }
        var en = new WindowEnumerator();
        var res = en.Enumerate(Process.GetCurrentProcess().Id);
        var report = new
        {
            schemaVersion = 1,
            timestampUtc = DateTime.UtcNow,
            monitorCount = res.Monitors.Count,
            monitors = res.Monitors.Select(m => new
            {
                device = m.DeviceName,
                primary = m.IsPrimary,
                bounds = new { m.Bounds.X, m.Bounds.Y, w = m.Bounds.Width, h = m.Bounds.Height },
                workArea = new { m.WorkArea.X, m.WorkArea.Y, w = m.WorkArea.Width, h = m.WorkArea.Height }
            }).ToList(),
            windowCount = res.Windows.Count,
            excludedCount = res.Excluded.Count,
            excludedReasonCounts = res.Excluded.GroupBy(e => e.Reason)
                .ToDictionary(g => g.Key, g => g.Count()),
            windows = res.Windows.Select(w => new
            {
                hwnd = w.Identity.Hwnd.ToInt64(),
                pid = w.Identity.Pid,
                startTicks = w.Identity.ProcessStartTicks,
                processName = w.ProcessName,
                exePath = w.ExePath,
                className = w.ClassName,
                title = w.Title,
                currentRect = new { w.CurrentRect.X, w.CurrentRect.Y, w = w.CurrentRect.Width, h = w.CurrentRect.Height },
                normalRect = new { w.NormalRect.X, w.NormalRect.Y, w = w.NormalRect.Width, h = w.NormalRect.Height },
                showState = w.ShowState.ToString(),
                monitorDevice = w.MonitorDevice
            }).ToList()
        };
        CliCommon.WriteJson(outPath, report);
        return 0;
    }
}
