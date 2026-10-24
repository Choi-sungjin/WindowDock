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
                case "--hotkey-smoke":
                    ApplicationConfiguration.Initialize();
                    return HotkeySmokeCommand.Run(args);
                case "--activation-smoke":
                    ApplicationConfiguration.Initialize();
                    return ActivationSmokeCommand.Run(args);
                case "--activation-client":
                    return ActivationSmokeCommand.RunClient(args);
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
            // Duplicate --start-in-tray (logon autostart) stays silent and hidden. A normal second
            // launch (e.g. the desktop shortcut) asks the running instance to show its window.
            var outcome = SingleInstanceActivation.HandleSecondInstance(
                startInTray, SingleInstanceActivation.ProductionEventName, TimeSpan.FromSeconds(3));
            if (outcome == SecondInstanceOutcome.ActivationUnavailable)
            {
                MessageBox.Show(
                    $"{AppInfo.ProductName}가 이미 실행 중이지만 그 창을 열지 못했습니다.\n작업 표시줄 오른쪽 트레이의 창자리 아이콘을 두 번 클릭하거나 '열기'를 눌러 주세요.",
                    AppInfo.ProductTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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

        // hotkeys.json lives beside settings.json but is a separate file, so settings persistence
        // can never erase slot/hotkey bindings. A missing file means "all keys off, slots empty".
        var hotkeys = new HotkeyStore(dataDir);
        var form = new MainForm(profiles, settings, monitor, provider, startInTray: startInTray, hotkeyStore: hotkeys);
        // Listen for "show yourself" from a second normal launch. Thread-pool wait; the callback only
        // marshals to the form's UI thread. Disposed after the message loop ends.
        ActivationListener? activation = null;
        try
        {
            activation = new ActivationListener(SingleInstanceActivation.ProductionEventName, () => form.RequestExternalActivation());
        }
        catch (Exception ex)
        {
            form.SetActivationListenerError(ex.GetType().Name);
        }
        try
        {
            Application.Run(form);
        }
        finally
        {
            activation?.Dispose();
        }
        return 0;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine($"{AppInfo.ProductTitle} — WindowLayoutManager.exe");
        Console.Error.WriteLine("  (no args)                                 GUI 실행 (이미 실행 중이면 기존 창을 엶)");
        Console.Error.WriteLine("  --start-in-tray                           GUI 실행하되 메인창을 숨긴 채 트레이에서만 시작");
        Console.Error.WriteLine("                                            (저장된 활성 프로파일/모니터링 상태 그대로, 기존 창은 이동하지 않음)");
        Console.Error.WriteLine("  --diagnose <out.json>                     현재 창/모니터 상태 (read-only)");
        Console.Error.WriteLine("  --save-current <name> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --validate-profile <id> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --apply-profile <id> --data-dir <d> --output <r>");
        Console.Error.WriteLine("  --self-test --output <r>");
        Console.Error.WriteLine("  --ui-smoke <png> --data-dir <d> [--output <r>]");
        Console.Error.WriteLine("  --tray-start-smoke --output <r>");
        Console.Error.WriteLine("  --activation-smoke --output <r>            (격리된 QA 이름 사용, 실행 중인 창자리에 신호 안 보냄)");
        Console.Error.WriteLine("  --hotkey-smoke --output <r> [--wait-os-hotkey <초>]");
        Console.Error.WriteLine("                                            (격리 임시 데이터 + 가상 창만 사용, 실제 사용자 창 미조작)");
        Console.Error.WriteLine("  --native-smoke --output <r> --data-dir <d>");
        Console.Error.WriteLine("  --test-window <marker> [--x --y --w --h --lifetime-ms]");
    }
}
