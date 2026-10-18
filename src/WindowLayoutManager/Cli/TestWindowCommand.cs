using System.Runtime.InteropServices;

namespace WindowLayoutManager.Cli;

public static class TestWindowCommand
{
    public const string FixtureClassName = "WLMTestFixtureClass";

    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--test-window requires <marker>"); return 2; }
        var marker = args[1];
        int x = int.Parse(CliCommon.GetArg(args, "--x") ?? "100");
        int y = int.Parse(CliCommon.GetArg(args, "--y") ?? "100");
        int w = int.Parse(CliCommon.GetArg(args, "--w") ?? "400");
        int h = int.Parse(CliCommon.GetArg(args, "--h") ?? "300");
        int lifetimeMs = int.Parse(CliCommon.GetArg(args, "--lifetime-ms") ?? "60000");
        int hideAfterMs = int.Parse(CliCommon.GetArg(args, "--hide-after-ms") ?? "0");
        int showAfterMs = int.Parse(CliCommon.GetArg(args, "--show-after-ms") ?? "0");
        string startState = CliCommon.GetArg(args, "--start-state") ?? "normal"; // normal|minimized|maximized
        // QA: keep the UI thread from pumping messages for N ms right after the window exists
        // (an unresponsive owned fixture). Keep below ~5 s so Windows does not ghost it.
        int hangMs = Math.Clamp(int.Parse(CliCommon.GetArg(args, "--hang-ms") ?? "0"), 0, 4500);
        _minTrackWidth = int.Parse(CliCommon.GetArg(args, "--min-track-w") ?? "0");
        _minTrackHeight = int.Parse(CliCommon.GetArg(args, "--min-track-h") ?? "0");
        _maxTrackWidth = int.Parse(CliCommon.GetArg(args, "--max-track-w") ?? "0");
        _maxTrackHeight = int.Parse(CliCommon.GetArg(args, "--max-track-h") ?? "0");

        var hInstance = GetModuleHandle(null);
        _wndProc = new WndProcDelegate(WindowProc);

        var wcx = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            lpszMenuName = null,
            lpszClassName = FixtureClassName,
            hIconSm = IntPtr.Zero
        };

        ushort atom = RegisterClassExW(ref wcx);
        if (atom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"RegisterClassExW failed err={err}");
            return 3;
        }

        var hwnd = CreateWindowExW(
            dwExStyle: 0,
            lpClassName: FixtureClassName,
            lpWindowName: $"WLM-Fixture [{marker}] pid={Environment.ProcessId}",
            dwStyle: WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            X: x, Y: y, nWidth: w, nHeight: h,
            hWndParent: IntPtr.Zero, hMenu: IntPtr.Zero, hInstance: hInstance, lpParam: IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"CreateWindowExW failed err={err}");
            return 3;
        }

        // Apply requested initial state (normal|minimized|maximized).
        switch (startState.ToLowerInvariant())
        {
            case "minimized": ShowWindow(hwnd, SW_SHOWMINIMIZED); break;
            case "maximized": ShowWindow(hwnd, SW_SHOWMAXIMIZED); break;
        }

        // Lifetime watchdog: post WM_CLOSE after lifetime expires.
        var dog = new Thread(() =>
        {
            Thread.Sleep(Math.Max(1000, lifetimeMs));
            PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true };
        dog.Start();

        // Optional hidden/shown lifecycle for native hidden-identity tests.
        if (hideAfterMs > 0)
        {
            var hider = new Thread(() =>
            {
                Thread.Sleep(hideAfterMs);
                ShowWindow(hwnd, SW_HIDE);
            }) { IsBackground = true };
            hider.Start();
        }
        if (showAfterMs > 0)
        {
            var shower = new Thread(() =>
            {
                Thread.Sleep(showAfterMs);
                ShowWindow(hwnd, SW_SHOWNA);
            }) { IsBackground = true };
            shower.Start();
        }

        if (hangMs > 0) Thread.Sleep(hangMs);

        // Standard message pump
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return 0;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate? _wndProc;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const int IDC_ARROW = 32512;
    private const int COLOR_WINDOW = 5;
    private const int SW_HIDE = 0;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_SHOWNA = 8;

    private static int _minTrackWidth;
    private static int _minTrackHeight;
    private static int _maxTrackWidth;
    private static int _maxTrackHeight;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            case WM_GETMINMAXINFO:
                if (_minTrackWidth > 0 || _minTrackHeight > 0 || _maxTrackWidth > 0 || _maxTrackHeight > 0)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    if (_minTrackWidth > 0) mmi.ptMinTrackSize.X = _minTrackWidth;
                    if (_minTrackHeight > 0) mmi.ptMinTrackSize.Y = _minTrackHeight;
                    if (_maxTrackWidth > 0) mmi.ptMaxTrackSize.X = _maxTrackWidth;
                    if (_maxTrackHeight > 0) mmi.ptMaxTrackSize.Y = _maxTrackHeight;
                    Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }
                break;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW([In] ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
