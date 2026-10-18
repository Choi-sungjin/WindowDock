using System.Diagnostics;
using WindowLayoutManager.App;
using WindowLayoutManager.Cli;
using WindowLayoutManager.Services;

namespace WindowLayoutManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--diagnose": return DiagnoseCommand.Run(args);
                case "--save-current": return SaveCurrentCommand.Run(args);
                case "--validate-profile": return ValidateCommand.Run(args);
                case "--apply-profile": return ApplyCommand.Run(args);
                case "--self-test": return SelfTestCommand.Run(args);
                case "--ui-smoke":
                    ApplicationConfiguration.Initialize();
                    return UiSmokeCommand.Run(args);
                case "--tray-start-smoke":
                    ApplicationConfiguration.Initialize();
                    return TrayStartSmokeCommand.Run(args);
                case "--native-smoke":
                    return NativeSmokeCommand.Run(args);
                case "--test-window":
                    return TestWindowCommand.Run(args);
                case "--start-in-tray":
                    return RunGui(startInTray: true);
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            return 2;
        }
        return RunGui(startInTray: false);
    }

    private static int RunGui(bool startInTray)
    {
        bool created;
        using var mutex = new Mutex(initiallyOwned: true, name: "Global\\WindowLayoutManager-SingleInstance", createdNew: out created);
        if (!created)
        {
            // Auto-start duplicates must stay silent (no dialog) — matches logon-triggered reinvocation.
            if (startInTray) return 0;
            MessageBox.Show("창자리 (WindowDock)가 이미 실행 중입니다.", "창자리 (WindowDock)", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        ApplicationConfiguration.Initialize();

        var dataDir = DataPaths.DefaultDataDir();
        var profiles = new ProfileStore(dataDir);
        var settings = new SettingsStore(dataDir);
        var provider = new ProductionWindowProvider(Process.GetCurrentProcess().Id);
        var monitor = new MonitoringService(provider, profiles, settings);
        // Prime seen+slots WITHOUT movement. Preserves the persisted active profile and the
        // persisted monitoring state exactly — never forces Resume or Apply at startup, so a
        // user who paused monitoring stays paused.
        monitor.InitializeFromSettings();

        var form = new MainForm(profiles, settings, monitor, provider, startInTray: startInTray);
        Application.Run(form);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("창자리 (WindowDock) — WindowLayoutManager.exe");
        Console.Error.WriteLine("  (no args)                                 GUI 실행");
        Console.Error.WriteLine("  --start-in-tray                           GUI 실행하되 메인창을 숨긴 채 트레이에서만 시작");
        Console.Error.WriteLine("                                            (저장된 활성 프로파일/모니터링 상태 그대로, 기존 창은 이동하지 않음)");
        Console.Error.WriteLine("  --diagnose <out.json>                     현재 창/모니터 상태 (read-only)");
        Console.Error.WriteLine("  --save-current <name> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --validate-profile <id> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --apply-profile <id> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --self-test --output <r>");
        Console.Error.WriteLine("  --ui-smoke <png> --data-dir <d> [--output <r>]");
        Console.Error.WriteLine("  --tray-start-smoke --output <r>");
        Console.Error.WriteLine("  --native-smoke --output <r> --data-dir <d>");
        Console.Error.WriteLine("  --test-window <marker> [--x --y --w --h --lifetime-ms]");
    }
}
