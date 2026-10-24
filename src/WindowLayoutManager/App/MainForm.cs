using System.Diagnostics;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.App;

public sealed class MainForm : Form
{
    private ProfileStore _profiles;
    private readonly ProfileStore _profilesOriginal;
    private readonly SettingsStore _settings;
    private readonly MonitoringService _monitor;
    private readonly IWindowProvider _provider;

    private readonly ListBox _profileList = new();
    private readonly ListView _entryList = new();
    private readonly ListView _resultList = new();
    private readonly PreviewPanel _preview = new();
    private readonly Label _statusLabel = new();
    private readonly Label _activeLabel = new();
    private readonly Label _monitorLabel = new();
    private readonly Label _emptyHelpLabel = new();
    private readonly Button _applyBtn = new();
    private readonly Button _pauseBtn = new();
    private readonly Button _hideBtn = new();
    private readonly Button _newBtn = new();
    private readonly Button _updateBtn = new();
    private readonly Button _renameBtn = new();
    private readonly Button _deleteBtn = new();
    private readonly Button _activateBtn = new();
    private readonly Button _editEntryBtn = new();
    private readonly Button _removeEntryBtn = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly System.Windows.Forms.Timer _timer;
    public ToolStripMenuItem TrayApplyMenu { get; }
    public ToolStripMenuItem TrayPauseItem { get; }
    public ToolStripMenuItem TrayOpenItem { get; }
    public ToolStripMenuItem TraySettingsItem { get; }
    public ToolStripMenuItem TrayExitItem { get; }
    private readonly Icon _trayIcon;
    private readonly bool _trayIconOwned;
    private int _externalActivationCount;
    private string? _activationListenerError;
    private volatile bool _activationReady;
    private int _pendingExternalActivation;

    private List<Profile> _profilesCache = new();
    private Profile? _selectedProfile;
    private bool _reallyExit = false;
    private bool _suppressModalDialogs = false;
    private readonly bool _startInTray;
    private bool _bootstrapped;
    private bool _firstShowConsumed;

    // --- v0.2.0 quick-apply slots / global hotkeys ---
    private readonly HotkeyStore _hotkeyStore;
    private GlobalHotkeyWindow? _hotkeyWindow;
    private HotkeyController? _hotkeyController;
    private readonly Button[] _slotButtons = new Button[HotkeyActions.SlotCount];
    private readonly Button _prevBtn = new();
    private readonly Button _nextBtn = new();
    private readonly Button _hotkeySettingsBtn = new();
    private readonly ToolTip _quickTips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };
    private TableLayoutPanel? _quickBar;
    private bool _quickApplyInProgress;
    private bool _hotkeyDialogOpen;
    private int _hotkeyInvocationCount;
    private int _quickApplyCount;
    private int _hotkeySuppressedCount;
    private int _hotkeyForwardedCount;
    private HotkeySettingsDialog? _openHotkeyDialog;

    public MainForm(ProfileStore profiles, SettingsStore settings, MonitoringService monitor, IWindowProvider provider)
        : this(profiles, settings, monitor, provider, startInTray: false) { }

    public MainForm(ProfileStore profiles, SettingsStore settings, MonitoringService monitor, IWindowProvider provider, bool startInTray)
        : this(profiles, settings, monitor, provider, startInTray, hotkeyStore: null) { }

    /// <summary>
    /// hotkeyStore null → hotkeys.json in the same data directory as the profile store. Loading a
    /// missing hotkeys.json never writes a file, so existing callers (UI/tray smoke) are unaffected.
    /// </summary>
    public MainForm(ProfileStore profiles, SettingsStore settings, MonitoringService monitor, IWindowProvider provider, bool startInTray, HotkeyStore? hotkeyStore)
    {
        _profiles = profiles;
        _profilesOriginal = profiles;
        _settings = settings;
        _monitor = monitor;
        _provider = provider;
        _startInTray = startInTray;
        _hotkeyStore = hotkeyStore ?? new HotkeyStore(Path.GetDirectoryName(profiles.ProfilesDir) ?? profiles.ProfilesDir);

        SuspendLayout();
        UiStyle.ApplyForm(this);
        Text = AppInfo.ProductTitle;
        Icon = AppIcon.ForForm; // shared process-lifetime icon; not owned/disposed by the form
        Size = new Size(1180, 760);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;
        if (_startInTray)
        {
            // Form must never flash onto the taskbar at startup; the user will Open it from the
            // tray if they want to see it. ShowInTaskbar flips back to true in ShowFromTray.
            ShowInTaskbar = false;
        }

        BuildLayout();

        _trayIcon = AppIcon.CreateTrayIcon(out _trayIconOwned);
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = AppInfo.ProductTitle,
            Icon = _trayIcon
        };
        _trayMenu = new ContextMenuStrip();
        TrayOpenItem = new ToolStripMenuItem("열기");
        TrayOpenItem.Click += (_, _) => ShowFromTray();
        TraySettingsItem = new ToolStripMenuItem("단축키 설정…");
        TraySettingsItem.Click += (_, _) => OpenHotkeySettingsFromTray();
        TrayApplyMenu = new ToolStripMenuItem("배치 적용");
        TrayPauseItem = new ToolStripMenuItem("자동 배치 일시정지");
        TrayPauseItem.Click += (_, _) => TogglePauseResume();
        TrayExitItem = new ToolStripMenuItem("종료");
        TrayExitItem.Click += (_, _) => ExitFromTray();
        _trayMenu.Items.Add(TrayOpenItem);
        _trayMenu.Items.Add(TraySettingsItem);
        _trayMenu.Items.Add(TrayApplyMenu);
        _trayMenu.Items.Add(TrayPauseItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(TrayExitItem);
        _trayMenu.Opening += (_, _) => RefreshTrayMenu();
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _monitor.LogAppended += (s) => BeginInvoke(new Action(() => AppendStatus(s)));
        _monitor.AutoReportsEmitted += (reports) => BeginInvoke(new Action(() => AppendAutoReports(reports)));

        _timer = new System.Windows.Forms.Timer { Interval = 800 };
        _timer.Tick += OnTimerTick;

        Load += (_, _) => Bootstrap();
        FormClosing += OnFormClosing;
        Disposed += (_, _) =>
        {
            ShutdownHotkeys();
            _quickTips.Dispose();
        };
        ResumeLayout(true);
    }

    /// <summary>
    /// Idempotent one-time startup bootstrap: load profiles, refresh header/tray menu, start the
    /// monitoring timer. Runs via the normal Load event in the standard GUI path, or synchronously
    /// via SetVisibleCore when the app starts in tray-only mode (where Load would otherwise never
    /// fire because the form never becomes visible). The force-touch of Handle ensures BeginInvoke
    /// (used by MonitoringService.AutoReportsEmitted) can post back even if the form stays hidden.
    /// This method NEVER applies a profile, NEVER resumes monitoring from a forced state, and
    /// NEVER launches a program — it only runs the exact same initialization the user already
    /// gets after Load, so the user's persisted pause choice is preserved.
    /// </summary>
    public void Bootstrap()
    {
        if (_bootstrapped) return;
        _bootstrapped = true;
        _ = this.Handle;
        ReloadProfiles();
        // Hotkeys are registered here (also in tray-only start). Loading/registering never applies
        // a layout or moves a window.
        InitializeHotkeys();
        RefreshQuickBar();
        RefreshTrayMenu();
        UpdateHeader();
        UpdateEmptyState();
        if (AppIcon.LoadError != null)
            AppendStatus($"앱 아이콘을 불러오지 못해 기본 아이콘을 사용합니다 ({AppIcon.LoadError}).");
        if (_activationListenerError != null)
            AppendStatus($"바로 가기로 다시 실행해도 이 창을 열 수 없습니다 ({_activationListenerError}). 트레이 아이콘의 '열기'를 사용하세요.");
        _timer.Start();
        MarkActivationReady();
    }

    /// <summary>
    /// When startInTray is set, intercept the very first Visible=true (triggered by Application.Run)
    /// so the form is never shown. We synchronously run Bootstrap before suppressing visibility so
    /// the tray icon, monitoring timer and persisted monitoring state are all live. Later calls
    /// (ShowFromTray → Show) go through base.SetVisibleCore(true) and display normally.
    ///
    /// IMPORTANT: only a Visible=TRUE call can consume the "first show" slot. An early Hide /
    /// Dispose propagates to SetVisibleCore(false) which must NOT mark the slot consumed, otherwise
    /// Application.Run's subsequent Visible=true would show the form.
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && _startInTray && !_firstShowConsumed)
        {
            _firstShowConsumed = true;
            Bootstrap();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    // v0.3.2 layout constants (logical px @96 DPI; see UiStyle).
    private const int SidebarWidth = 300;
    private const int SidebarMinWidth = 250;
    private const int ContentMinWidth = 440;
    private const int GroupButtonRow = UiStyle.ButtonHeight + UiStyle.Gap; // button + one gap
    private const int CaptionRow = 30;
    private const int FooterButtonWidth = 230;
    private const int EntryButtonWidth = 140;
    private const int NavButtonWidth = 150;

    /// <summary>
    /// Layout (top → bottom): header (active / monitoring / status), quick area (8 equal slot
    /// buttons; then a caption + three equal action buttons), split content (sidebar: caption,
    /// saved list, five stacked full-width actions | entries + results / preview), footer (three
    /// equal actions on one baseline). Every outer edge is inset by UiStyle.OuterPadding and every
    /// button has UiStyle.ButtonHeight; no FlowLayoutPanel wrapping.
    /// </summary>
    private void BuildLayout()
    {
        int pad = UiStyle.OuterPadding, half = UiStyle.HalfGap;

        // ---------- header ----------
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(pad - half, 4, pad - half, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        foreach (var l in new[] { _activeLabel, _monitorLabel, _statusLabel })
        {
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;   // long text is cut with … inside its own column
            l.UseMnemonic = false;
            l.Margin = new Padding(half, 0, half, 0);
        }
        _activeLabel.Font = UiStyle.BoldFont;
        _statusLabel.ForeColor = SystemColors.GrayText;
        header.Controls.Add(_activeLabel, 0, 0);
        header.Controls.Add(_monitorLabel, 1, 0);
        header.Controls.Add(_statusLabel, 2, 0);

        // ---------- footer: three equal actions on one baseline ----------
        UiStyle.ConfigureButton(_applyBtn, "지금 적용 + 자동 배치 시작");
        UiStyle.ConfigureButton(_pauseBtn, "자동 배치 시작");
        UiStyle.ConfigureButton(_hideBtn, "트레이로 숨기기");
        _applyBtn.Click += (_, _) => DoApply();
        _pauseBtn.Click += (_, _) => TogglePauseResume();
        _hideBtn.Click += (_, _) => HideToTray();
        var bottom = UiStyle.Footer(
            new (Button, int)[] { (_applyBtn, FooterButtonWidth), (_pauseBtn, FooterButtonWidth), (_hideBtn, FooterButtonWidth) },
            Array.Empty<(Button, int)>());

        // ---------- content split ----------
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1 };

        // Sidebar: caption, saved list, five full-width identical actions.
        var leftLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(pad - half, half, half, half)
        };
        leftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, CaptionRow));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 5; i++) leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, GroupButtonRow));
        leftLayout.Controls.Add(UiStyle.Caption("저장된 배치"), 0, 0);

        _profileList.Dock = DockStyle.Fill;
        _profileList.IntegralHeight = false;
        _profileList.Margin = new Padding(half);
        _profileList.SelectedIndexChanged += (_, _) => OnProfileSelected();
        leftLayout.Controls.Add(_profileList, 0, 1);

        UiStyle.ConfigureButton(_newBtn, "현재 창에서 새로 저장");
        UiStyle.ConfigureButton(_updateBtn, "선택 배치 업데이트");
        UiStyle.ConfigureButton(_activateBtn, "활성 배치로 선택");
        UiStyle.ConfigureButton(_renameBtn, "이름 변경");
        UiStyle.ConfigureButton(_deleteBtn, "삭제");
        _newBtn.Click += (_, _) => DoCreateFromCurrent();
        _updateBtn.Click += (_, _) => DoUpdateFromCurrent();
        _activateBtn.Click += (_, _) => DoActivateSelected();
        _renameBtn.Click += (_, _) => DoRename();
        _deleteBtn.Click += (_, _) => DoDelete();
        int row = 2;
        foreach (var b in new[] { _newBtn, _updateBtn, _activateBtn, _renameBtn, _deleteBtn })
        {
            leftLayout.Controls.Add(b, 0, row++);
        }
        split.Panel1.Controls.Add(leftLayout);

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.None };

        // Entries: caption row with two equal item actions on the right, entry list, results.
        var entryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(half, half, pad - half, 0)
        };
        entryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, GroupButtonRow));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, CaptionRow));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));

        var entryHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        entryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        entryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, EntryButtonWidth + UiStyle.Gap));
        entryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, EntryButtonWidth + UiStyle.Gap));
        entryHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        entryHeader.Controls.Add(UiStyle.Caption("배치 항목"), 0, 0);
        UiStyle.ConfigureButton(_editEntryBtn, "항목 편집");
        UiStyle.ConfigureButton(_removeEntryBtn, "항목 삭제");
        _editEntryBtn.Click += (_, _) => DoEditEntry();
        _removeEntryBtn.Click += (_, _) => DoRemoveEntry();
        entryHeader.Controls.Add(_editEntryBtn, 1, 0);
        entryHeader.Controls.Add(_removeEntryBtn, 2, 0);
        entryLayout.Controls.Add(entryHeader, 0, 0);

        _entryList.Dock = DockStyle.Fill;
        _entryList.Margin = new Padding(half);
        _entryList.View = View.Details;
        _entryList.FullRowSelect = true;
        _entryList.GridLines = true;
        _entryList.Columns.Add("역할", LogicalToDeviceUnits(150));
        _entryList.Columns.Add("프로그램", LogicalToDeviceUnits(130));
        _entryList.Columns.Add("타이틀 포함", LogicalToDeviceUnits(160));
        _entryList.Columns.Add("배치 위치/크기", LogicalToDeviceUnits(180));
        _entryList.Columns.Add("상태", LogicalToDeviceUnits(90));
        _entryList.MultiSelect = false;
        _entryList.DoubleClick += (_, _) => DoEditEntry();
        entryLayout.Controls.Add(_entryList, 0, 1);

        entryLayout.Controls.Add(UiStyle.Caption("최근 적용/자동 배치 결과"), 0, 2);
        _resultList.Dock = DockStyle.Fill;
        _resultList.Margin = new Padding(half);
        _resultList.View = View.Details;
        _resultList.FullRowSelect = true;
        _resultList.GridLines = true;
        _resultList.Columns.Add("시간", LogicalToDeviceUnits(80));
        _resultList.Columns.Add("역할", LogicalToDeviceUnits(140));
        _resultList.Columns.Add("결과", LogicalToDeviceUnits(110));
        _resultList.Columns.Add("설명", LogicalToDeviceUnits(400));
        entryLayout.Controls.Add(_resultList, 0, 3);

        // Preview with the first-run help line.
        var previewWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(half, half, pad - half, half)
        };
        previewWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        previewWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        previewWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _emptyHelpLabel.AutoSize = false;
        _emptyHelpLabel.Dock = DockStyle.Fill;
        _emptyHelpLabel.TextAlign = ContentAlignment.MiddleLeft;
        _emptyHelpLabel.ForeColor = SystemColors.GrayText;
        _emptyHelpLabel.Margin = new Padding(half, 0, half, 0);
        _emptyHelpLabel.AutoEllipsis = true;
        _emptyHelpLabel.Text = "창을 원하는 자리에 배치 후 '현재 창에서 새로 저장'을 누르세요. 이후 '지금 적용'을 누르면 그 배치로 되돌리고, 같은 프로그램의 새 창을 자동으로 그 자리에 놓습니다.";
        _preview.Dock = DockStyle.Fill;
        _preview.Margin = new Padding(half);
        previewWrap.Controls.Add(_emptyHelpLabel, 0, 0);
        previewWrap.Controls.Add(_preview, 0, 1);

        rightSplit.Panel1.Controls.Add(entryLayout);
        rightSplit.Panel2.Controls.Add(previewWrap);
        split.Panel2.Controls.Add(rightSplit);

        _quickBar = BuildQuickBar();

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(_quickBar);
        Controls.Add(header);

        HandleCreated += (_, _) =>
        {
            try
            {
                // Splitter values are not auto-scaled → convert logical px for this DPI.
                split.Panel1MinSize = LogicalToDeviceUnits(SidebarMinWidth);
                split.Panel2MinSize = LogicalToDeviceUnits(ContentMinWidth);
                if (split.Width > LogicalToDeviceUnits(SidebarWidth + ContentMinWidth)) split.SplitterDistance = LogicalToDeviceUnits(SidebarWidth);
                int h = rightSplit.Height;
                if (h > LogicalToDeviceUnits(300))
                {
                    rightSplit.SplitterDistance = Math.Min(h - LogicalToDeviceUnits(150), Math.Max(LogicalToDeviceUnits(240), (int)(h * 0.62)));
                }
            }
            catch { }
        };
    }

    /// <summary>
    /// Quick area, two rows. Row 1: the eight slot buttons, equal width across the content width
    /// (number first; long names end-ellipsized; full name in tooltip/AccessibleName).
    /// Row 2: a short caption on the left and three equal action buttons (이전 / 다음 / 단축키 설정).
    /// </summary>
    private TableLayoutPanel BuildQuickBar()
    {
        int pad = UiStyle.OuterPadding, half = UiStyle.HalfGap;
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 2 * GroupButtonRow + 2 * half,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(pad - half, half, pad - half, half),
            AccessibleName = "빠른 적용"
        };
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, GroupButtonRow));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, GroupButtonRow));

        var slots = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = HotkeyActions.SlotCount,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AccessibleName = "빠른 적용 슬롯"
        };
        for (int i = 0; i < HotkeyActions.SlotCount; i++) slots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / HotkeyActions.SlotCount));
        slots.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (int i = 0; i < HotkeyActions.SlotCount; i++)
        {
            int n = i + 1;
            var b = UiStyle.ConfigureButton(new Button { AccessibleName = $"슬롯 {n}" }, $"{n}  (비어 있음)");
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Click += (_, _) => PerformSlotApply(n, fromHotkey: false);
            _slotButtons[i] = b;
            slots.Controls.Add(b, i, 0);
        }
        area.Controls.Add(slots, 0, 0);

        var nav = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 3; i++) nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavButtonWidth + UiStyle.Gap));
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        nav.Controls.Add(UiStyle.CellLabel("숫자 버튼: 지정한 배치를 바로 적용 · 이전/다음: 저장 목록 순서로 적용", SystemColors.GrayText), 0, 0);
        UiStyle.ConfigureButton(_prevBtn, "◀ 이전 배치");
        UiStyle.ConfigureButton(_nextBtn, "다음 배치 ▶");
        UiStyle.ConfigureButton(_hotkeySettingsBtn, "단축키 설정…");
        _prevBtn.Click += (_, _) => PerformNavigateApply(next: false, fromHotkey: false);
        _nextBtn.Click += (_, _) => PerformNavigateApply(next: true, fromHotkey: false);
        _hotkeySettingsBtn.Click += (_, _) => DoHotkeySettings();
        nav.Controls.Add(_prevBtn, 1, 0);
        nav.Controls.Add(_nextBtn, 2, 0);
        nav.Controls.Add(_hotkeySettingsBtn, 3, 0);
        area.Controls.Add(nav, 0, 1);
        return area;
    }

    private void ReloadProfiles()
    {
        _profilesCache = _profiles.LoadAll(out var errs);
        foreach (var e in errs) AppendStatus($"배치 로드 오류: {e}");
        if (!string.IsNullOrEmpty(_settings.LastLoadError))
        {
            AppendStatus($"설정 로드 오류: {_settings.LastLoadError}");
        }

        var curId = (_profileList.SelectedItem as ProfileRow)?.Profile.Id ?? _selectedProfile?.Id;
        _profileList.BeginUpdate();
        _profileList.Items.Clear();
        foreach (var p in ProfileOrdering.Sort(_profilesCache))
        {
            _profileList.Items.Add(new ProfileRow(p, p.Id == _monitor.ActiveProfile?.Id));
        }
        _profileList.EndUpdate();

        if (curId != null)
        {
            for (int i = 0; i < _profileList.Items.Count; i++)
            {
                if (((ProfileRow)_profileList.Items[i]!).Profile.Id == curId)
                {
                    _profileList.SelectedIndex = i; break;
                }
            }
        }
        else if (_profileList.Items.Count > 0)
        {
            _profileList.SelectedIndex = 0;
        }
        else
        {
            _selectedProfile = null;
            _entryList.Items.Clear();
            _preview.Set(Array.Empty<ProfileEntry>(), WindowEnumerator.EnumerateMonitors());
        }
        UpdateEmptyState();
        // Slot labels follow renames (bindings are by stable profile ID).
        RefreshQuickBar();
    }

    private void OnProfileSelected()
    {
        _selectedProfile = (_profileList.SelectedItem as ProfileRow)?.Profile;
        _entryList.BeginUpdate();
        _entryList.Items.Clear();
        if (_selectedProfile != null)
        {
            var slots = _monitor.SlotSnapshot();
            foreach (var e in _selectedProfile.Entries)
            {
                var r = e.Rect;
                var geom = $"{r.X},{r.Y} {r.Width}x{r.Height}";
                if (e.Maximized) geom += " [최대화]";
                string state = _monitor.ActiveProfile?.Id == _selectedProfile.Id && slots.ContainsKey(e.Id)
                    ? "창 연결됨"
                    : "대기";
                _entryList.Items.Add(new ListViewItem(new[]
                {
                    e.Role, e.ProcessName, e.TitleContains ?? "(모든 창)", geom, state
                }) { Tag = e });
            }
        }
        _entryList.EndUpdate();
        _preview.Set(_selectedProfile?.Entries ?? Enumerable.Empty<ProfileEntry>(), WindowEnumerator.EnumerateMonitors());
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = _profilesCache.Count == 0;
        _emptyHelpLabel.Visible = empty;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _monitor.Tick();
        }
        catch (Exception ex)
        {
            AppendStatus("타이머 오류: " + ex.Message);
        }
    }

    private void UpdateHeader()
    {
        _activeLabel.Text = _monitor.ActiveProfile is null ? "활성 배치: 없음" : $"활성 배치: {_monitor.ActiveProfile.Name}";
        _quickTips.SetToolTip(_activeLabel, _activeLabel.Text);
        _monitorLabel.Text = _monitor.IsMonitoring ? "자동 배치: 켜짐 ●" : "자동 배치: 꺼짐 ○";
        _monitorLabel.ForeColor = _monitor.IsMonitoring ? Color.ForestGreen : SystemColors.GrayText;
        _pauseBtn.Text = _monitor.IsMonitoring ? "자동 배치 일시정지" : "자동 배치 시작";
        TrayPauseItem.Text = _monitor.IsMonitoring ? "자동 배치 일시정지" : "자동 배치 시작";
    }

    private void RefreshTrayMenu()
    {
        TrayApplyMenu.DropDownItems.Clear();
        foreach (var p in ProfileOrdering.Sort(_profilesCache))
        {
            var captured = p;
            var item = new ToolStripMenuItem(p.Name) { Tag = p };
            item.Click += (_, _) =>
            {
                // The tray menu stays usable while a confirmation/edit modal is open on the main
                // form; applying then would change the active/selected profile under that modal.
                if (IsExplicitApplyBlocked(out var why))
                {
                    AppendStatus($"트레이 적용을 하지 않았습니다: {why}. 창을 닫은 뒤 다시 시도하세요.");
                    return;
                }
                _monitor.SetActiveProfile(captured);
                var reports = _monitor.ApplyActiveProfile();
                BeginInvoke(new Action(() =>
                {
                    ReloadProfiles();
                    UpdateHeader();
                    AppendStatus($"적용: {captured.Name} — 이동 {reports.Count(r => r.Result == PlaceResult.Moved)}");
                    AppendApplyReports(reports);
                }));
            };
            TrayApplyMenu.DropDownItems.Add(item);
        }
        if (TrayApplyMenu.DropDownItems.Count == 0)
        {
            TrayApplyMenu.DropDownItems.Add(new ToolStripMenuItem("(저장된 배치 없음)") { Enabled = false });
        }
    }

    private void DoCreateFromCurrent()
    {
        var snap = _provider.Enumerate();
        if (snap.Windows.Count == 0)
        {
            MessageBox.Show(this, "현재 저장 가능한 창을 찾지 못했습니다.", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new WindowPickerDialog(_provider, snap.Windows, $"배치-{DateTime.Now:MMdd-HHmm}");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var profile = new Profile
        {
            Id = ProfileStore.GenerateId(),
            Name = dlg.ProfileName,
            Entries = dlg.Picked.Select(p => BuildEntry(p.Fresh, p.Role, p.TitleContains)).ToList()
        };
        try
        {
            _profiles.Save(profile);
            ReloadProfiles();
            RefreshTrayMenu();
            AppendStatus($"저장: {profile.Name} ({profile.Entries.Count}개 항목)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "저장 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Default production confirmation uses a Windows MessageBox.</summary>
    private bool DefaultUpdateConfirm(string message)
    {
        return MessageBox.Show(this, message, "업데이트 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>Optional override confirmation handler. Null → use DefaultUpdateConfirm (MessageBox).
    /// Used by the UI smoke harness so the real Update button path can be driven unattended.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, bool>? UpdateConfirmHandler { get; set; }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public UpdateOutcome? LastUpdateOutcome { get; private set; }

    private void DoUpdateFromCurrent()
    {
        LastUpdateOutcome = PerformUpdateFromCurrent(UpdateConfirmHandler ?? DefaultUpdateConfirm);
    }

    /// <summary>
    /// Transactional update: builds a detached proposal, asks the caller-provided confirmation
    /// delegate for approval, then persists; only after a successful save does it copy the
    /// proposed geometry back into the live in-memory profile. Cancelling or save failure leaves
    /// the live model untouched. The injectable confirm callback keeps this testable; the
    /// production button path uses the Windows MessageBox default.
    /// </summary>
    public UpdateOutcome PerformUpdateFromCurrent(Func<string, bool> confirm)
    {
        if (_selectedProfile is null)
        {
            NotifyInfo("먼저 배치를 선택하세요.");
            return UpdateOutcome.NoSelection;
        }
        var snap = _provider.Enumerate();
        if (snap.Windows.Count == 0)
        {
            NotifyInfo("현재 저장 가능한 창을 찾지 못했습니다.");
            return UpdateOutcome.NoWindows;
        }

        // The confirmation below is modal; act on exactly this profile afterwards.
        var target = _selectedProfile;
        var proposal = ProfileUpdater.Build(target, snap.Windows);
        var changeLog = string.Join("\n", proposal.Updates.Select(u => u.Action switch
        {
            UpdateAction.WouldUpdate => $"- {u.Role}: {u.NewRect.X},{u.NewRect.Y} {u.NewRect.Width}x{u.NewRect.Height}" + (u.NewMaximized ? " [최대화]" : ""),
            UpdateAction.PreserveAmbiguous => $"- {u.Role}: 구분 불가 (보존)",
            _ => $"- {u.Role}: 현재 열린 창 없음 (보존)"
        }));
        var msg = $"선택 배치를 현재 창 위치로 업데이트합니다. 계속하시겠습니까?\n\n업데이트: {proposal.UpdatedCount}개\n보존(닫혀 있음): {proposal.PreservedNoMatchCount}개\n보존(구분 불가): {proposal.PreservedAmbiguousCount}개\n\n{changeLog}";

        if (!confirm(msg)) return UpdateOutcome.Cancelled;

        try
        {
            _profiles.Save(proposal.Proposed);
        }
        catch (Exception ex)
        {
            NotifyError("저장 실패: " + ex.Message);
            return UpdateOutcome.SaveFailed;
        }

        // Save succeeded. Copy proposed contents into the live model (keeps object identity so UI
        // references stay valid), then refresh monitor if this is the active profile.
        ProfileUpdater.CopyInto(target, proposal.Proposed);
        if (_monitor.ActiveProfile?.Id == target.Id)
        {
            _monitor.RefreshActiveFromDisk();
        }
        ReloadProfiles();
        RefreshTrayMenu();
        AppendStatus($"업데이트: {target.Name} (변경 {proposal.UpdatedCount}, 보존 {proposal.PreservedNoMatchCount + proposal.PreservedAmbiguousCount})");
        return UpdateOutcome.Applied;
    }

    public enum UpdateOutcome { NoSelection, NoWindows, Cancelled, SaveFailed, Applied }

    private static ProfileEntry BuildEntry(WindowInfo w, string role, string? titleContains)
    {
        // Store NormalRect for both minimized AND maximized to preserve restore geometry;
        // use CurrentRect only when the window is in Normal state.
        var saveRect = w.ShowState == WindowShowState.Normal ? w.CurrentRect : w.NormalRect;
        return new ProfileEntry
        {
            Id = ProfileStore.GenerateId(),
            Role = role,
            ExePath = w.ExePath,
            ProcessName = w.ProcessName,
            ClassName = w.ClassName,
            TitleContains = titleContains,
            Rect = SavedRect.From(saveRect),
            Maximized = w.ShowState == WindowShowState.Maximized
        };
    }

    private void DoActivateSelected()
    {
        if (_selectedProfile is null) return;
        _monitor.SetActiveProfile(_selectedProfile);
        ReloadProfiles();
        UpdateHeader();
        AppendStatus($"활성 배치 설정: {_selectedProfile.Name} (자동 배치는 '지금 적용'을 눌러야 켜집니다)");
    }

    private void DoApply()
    {
        if (_selectedProfile is null)
        {
            MessageBox.Show(this, "먼저 배치를 선택하세요.", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _monitor.SetActiveProfile(_selectedProfile);
        var reports = _monitor.ApplyActiveProfile();
        ReloadProfiles();
        UpdateHeader();
        int moved = reports.Count(r => r.Result == PlaceResult.Moved);
        int skipped = reports.Count - moved;
        AppendStatus($"적용 완료: 이동 {moved}, 건너뜀 {skipped} — 자동 배치 켜짐");
        AppendApplyReports(reports);
    }

    private void DoRename()
    {
        if (_selectedProfile is null) return;
        var targetId = _selectedProfile.Id;
        using var dlg = new InputDialog("배치 이름 변경", "새 이름:", _selectedProfile.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (_selectedProfile?.Id != targetId) { NotifyError("선택된 배치가 바뀌어 이름 변경을 취소했습니다."); return; }
        PerformRename(dlg.Value);
    }

    /// <summary>
    /// Transactional rename: builds a detached clone with the new name, saves, and only on
    /// successful persist copies the new name into the live model. On save failure the live model
    /// AND disk remain untouched (the user sees the error and the old name).
    /// </summary>
    public UpdateOutcome PerformRename(string newName)
    {
        if (_selectedProfile is null) return UpdateOutcome.NoSelection;
        var clone = ProfileUpdater.CloneProfile(_selectedProfile);
        clone.Name = newName;
        try
        {
            _profiles.Save(clone);
        }
        catch (Exception ex)
        {
            NotifyError("저장 실패: " + ex.Message);
            return UpdateOutcome.SaveFailed;
        }
        _selectedProfile.Name = newName;
        if (_monitor.ActiveProfile?.Id == _selectedProfile.Id)
        {
            _monitor.RefreshActiveFromDisk();
        }
        ReloadProfiles();
        RefreshTrayMenu();
        return UpdateOutcome.Applied;
    }

    private void DoDelete()
    {
        if (_selectedProfile is null) return;
        // Act on exactly the profile named in the confirmation, even if selection changes meanwhile.
        var target = _selectedProfile;
        if (MessageBox.Show(this, $"'{target.Name}' 배치를 삭제하시겠습니까?", "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _profiles.Delete(target.Id);
        if (_monitor.ActiveProfile?.Id == target.Id)
        {
            _monitor.SetActiveProfile(null);
        }
        _selectedProfile = null;
        ReloadProfiles();
        RefreshTrayMenu();
        UpdateHeader();
    }

    private void DoEditEntry()
    {
        if (_selectedProfile is null || _entryList.SelectedItems.Count == 0) return;
        var entry = (ProfileEntry)_entryList.SelectedItems[0].Tag!;
        var targetId = _selectedProfile.Id;
        using var dlg = new EntryEditDialog(entry);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (_selectedProfile?.Id != targetId) { NotifyError("선택된 배치가 바뀌어 항목 편집을 취소했습니다."); return; }
        PerformEditEntry(entry.Id, dlg.RoleResult, dlg.TitleContainsResult, dlg.RectResult, dlg.MaximizedResult);
    }

    /// <summary>
    /// Transactional entry edit: builds a detached clone with the edit applied, saves, and only on
    /// successful persist copies the fields into the live entry. Preserves entry Id and any other
    /// fields not changed by the dialog (exe path, class name).
    /// </summary>
    public UpdateOutcome PerformEditEntry(string entryId, string role, string? titleContains, SavedRect rect, bool maximized)
    {
        if (_selectedProfile is null) return UpdateOutcome.NoSelection;
        var clone = ProfileUpdater.CloneProfile(_selectedProfile);
        var cEntry = clone.Entries.FirstOrDefault(e => e.Id == entryId);
        if (cEntry is null) return UpdateOutcome.NoSelection;
        cEntry.Role = role;
        cEntry.TitleContains = titleContains;
        cEntry.Rect = new SavedRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
        cEntry.Maximized = maximized;
        try
        {
            _profiles.Save(clone);
        }
        catch (Exception ex)
        {
            NotifyError("저장 실패: " + ex.Message);
            return UpdateOutcome.SaveFailed;
        }
        var liveEntry = _selectedProfile.Entries.First(e => e.Id == entryId);
        liveEntry.Role = role;
        liveEntry.TitleContains = titleContains;
        liveEntry.Rect = new SavedRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
        liveEntry.Maximized = maximized;
        if (_monitor.ActiveProfile?.Id == _selectedProfile.Id)
        {
            _monitor.RefreshActiveFromDisk();
        }
        OnProfileSelected();
        return UpdateOutcome.Applied;
    }

    private void DoRemoveEntry()
    {
        if (_selectedProfile is null || _entryList.SelectedItems.Count == 0) return;
        var entry = (ProfileEntry)_entryList.SelectedItems[0].Tag!;
        var targetId = _selectedProfile.Id;
        if (MessageBox.Show(this, $"'{entry.Role}' 항목을 삭제하시겠습니까?", "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (_selectedProfile?.Id != targetId) { NotifyError("선택된 배치가 바뀌어 항목 삭제를 취소했습니다."); return; }
        PerformRemoveEntry(entry.Id);
    }

    /// <summary>
    /// Transactional entry removal: builds a detached clone with the entry removed, saves, and
    /// only on successful persist removes the entry from the live model.
    /// </summary>
    public UpdateOutcome PerformRemoveEntry(string entryId)
    {
        if (_selectedProfile is null) return UpdateOutcome.NoSelection;
        var clone = ProfileUpdater.CloneProfile(_selectedProfile);
        clone.Entries.RemoveAll(e => e.Id == entryId);
        try
        {
            _profiles.Save(clone);
        }
        catch (Exception ex)
        {
            NotifyError("저장 실패: " + ex.Message);
            return UpdateOutcome.SaveFailed;
        }
        _selectedProfile.Entries.RemoveAll(e => e.Id == entryId);
        if (_monitor.ActiveProfile?.Id == _selectedProfile.Id)
        {
            _monitor.RefreshActiveFromDisk();
        }
        OnProfileSelected();
        return UpdateOutcome.Applied;
    }

    // ===================== v0.2.0 quick apply + global hotkeys =====================

    private void InitializeHotkeys()
    {
        try
        {
            _hotkeyWindow = new GlobalHotkeyWindow();
            _hotkeyWindow.HotkeyMessage += OnHotkeyMessage;
            _hotkeyController = new HotkeyController(_hotkeyStore, _hotkeyWindow);
            _hotkeyController.Initialize();
            if (_hotkeyController.LoadError != null)
            {
                AppendStatus($"단축키 설정 문제: {_hotkeyController.LoadError} — 모든 전역 단축키를 끈 기본값으로 시작합니다. 원본 파일은 그대로 두거나 .bad 사본으로 보존합니다.");
            }
            var failed = _hotkeyController.Statuses
                .Where(kv => kv.Value.State == HotkeyRegState.Failed)
                .Select(kv => HotkeyActions.KoreanLabel(kv.Key))
                .ToList();
            if (failed.Count > 0)
            {
                AppendStatus($"전역 단축키 등록 실패: {string.Join(", ", failed)} — 다른 프로그램이 같은 조합을 사용 중일 수 있습니다. 버튼은 계속 사용할 수 있습니다.");
            }
        }
        catch (Exception ex)
        {
            AppendStatus("전역 단축키 초기화 실패: " + ex.GetType().Name + " — 빠른 적용 버튼은 계속 사용할 수 있습니다.");
        }
    }

    private void ShutdownHotkeys()
    {
        try { _hotkeyController?.Shutdown(); } catch { }
        try { _hotkeyWindow?.Dispose(); } catch { }
        _hotkeyWindow = null;
    }

    /// <summary>
    /// WM_HOTKEY from our message-only window.
    ///  - Ids we do not currently hold (e.g. queued just before the key was disabled) are ignored.
    ///  - While the hotkey settings dialog is open, a registered combination is FORWARDED to the
    ///    focused key-capture field instead of applying (RegisterHotKey swallows the key before the
    ///    field would see it). Registrations are not touched, so Cancel / save failure keep them.
    ///  - While ANY modal is open on this form (native MessageBox, WinForms ShowDialog, ...) the
    ///    command is dropped — not queued — and counted as suppressed.
    ///  - Otherwise the apply is deferred out of the window procedure via BeginInvoke and the modal
    ///    check is repeated at execution time.
    /// MOD_NOREPEAT means holding the key does not repeat; the in-progress guard prevents overlap.
    /// </summary>
    private void OnHotkeyMessage(int id)
    {
        if (_hotkeyController is null) return;
        if (!HotkeyActions.TryFromRegistrationId(id, out var action)) return;
        if (!_hotkeyController.IsRegistered(action)) return;

        if (_hotkeyDialogOpen)
        {
            var b = _hotkeyController.Current.Get(action);
            if (_openHotkeyDialog is { } dlg && dlg.TryForwardRegisteredCombo(b.Ctrl, b.Alt, b.Shift, b.Key))
            {
                _hotkeyForwardedCount++;
                return;
            }
            NoteHotkeySuppressed(action, "단축키 설정 창이 열려 있음");
            return;
        }
        if (IsExplicitApplyBlocked(out var why))
        {
            NoteHotkeySuppressed(action, why);
            return;
        }
        _hotkeyInvocationCount++;
        void Run()
        {
            // Re-check at execution time: a modal may have opened between receipt and dispatch.
            if (IsExplicitApplyBlocked(out var whyLater))
            {
                NoteHotkeySuppressed(action, whyLater);
                return;
            }
            ExecuteQuickAction(action, fromHotkey: true);
        }
        try
        {
            BeginInvoke(new Action(Run));
        }
        catch (InvalidOperationException)
        {
            Run();
        }
    }

    /// <summary>
    /// True while this form is owned by a modal UI. Native MessageBox/DialogBox and WinForms
    /// ShowDialog disable the owner with EnableWindow, which the managed Enabled property does not
    /// necessarily mirror, so the native IsWindowEnabled state is checked as well.
    /// </summary>
    private bool IsExplicitApplyBlocked(out string reason)
    {
        if (_hotkeyDialogOpen) { reason = "단축키 설정 창이 열려 있음"; return true; }
        if (IsHandleCreated && !NativeMethods.IsWindowEnabled(Handle)) { reason = "확인/편집 창이 열려 있음"; return true; }
        if (!Enabled) { reason = "확인/편집 창이 열려 있음"; return true; }
        foreach (Form f in Application.OpenForms)
        {
            if (!ReferenceEquals(f, this) && f.Modal && f.Visible) { reason = "확인/편집 창이 열려 있음"; return true; }
        }
        if (_quickApplyInProgress) { reason = "이전 적용이 진행 중"; return true; }
        reason = "";
        return false;
    }

    private void NoteHotkeySuppressed(HotkeyActionId action, string reason)
    {
        _hotkeySuppressedCount++;
        AppendStatus($"{HotkeyActions.KoreanLabel(action)} 단축키를 무시했습니다: {reason}. 창을 닫은 뒤 다시 누르세요.");
    }

    private QuickApplyResult ExecuteQuickAction(HotkeyActionId action, bool fromHotkey) => action switch
    {
        HotkeyActionId.Previous => PerformNavigateApply(next: false, fromHotkey),
        HotkeyActionId.Next => PerformNavigateApply(next: true, fromHotkey),
        _ => PerformSlotApply(HotkeyActions.SlotNumber(action), fromHotkey)
    };

    /// <summary>Same as clicking slot button n (1..8): applies the assigned profile even when its
    /// global hotkey is disabled.</summary>
    public QuickApplyResult PerformSlotApply(int slotNumber) => PerformSlotApply(slotNumber, fromHotkey: false);

    private QuickApplyResult PerformSlotApply(int slotNumber, bool fromHotkey)
    {
        var action = HotkeyActions.FromSlotNumber(slotNumber);
        var cfg = _hotkeyController?.Current ?? HotkeyConfig.CreateDefault();
        return RunQuickApply(cfg.Get(action).ProfileId, $"슬롯 {slotNumber}", fromHotkey);
    }

    /// <summary>Same as clicking Previous/Next: next/previous over ALL saved layouts in list order.</summary>
    public QuickApplyResult PerformNavigateApply(bool next) => PerformNavigateApply(next, fromHotkey: false);

    private QuickApplyResult PerformNavigateApply(bool next, bool fromHotkey)
    {
        var label = next ? "다음 배치" : "이전 배치";
        if (_quickApplyInProgress) return ReportQuickResult(QuickApplyResult.Of(QuickApplyOutcome.Busy), label, fromHotkey);
        ReloadProfiles();
        var ids = ProfileOrdering.Sort(_profilesCache).Select(p => p.Id).ToList();
        var target = ProfileOrdering.ResolveNeighbor(ids, _monitor.ActiveProfile?.Id, _selectedProfile?.Id, next);
        if (target is null) return ReportQuickResult(QuickApplyResult.Of(QuickApplyOutcome.NoProfiles), label, fromHotkey);
        return RunQuickApply(target, label, fromHotkey);
    }

    private QuickApplyResult RunQuickApply(string? profileId, string label, bool fromHotkey)
    {
        if (_quickApplyInProgress) return ReportQuickResult(QuickApplyResult.Of(QuickApplyOutcome.Busy), label, fromHotkey);
        _quickApplyInProgress = true;
        try
        {
            QuickApplyResult result;
            try
            {
                result = QuickApply.ApplyById(_profiles, _monitor, profileId);
            }
            catch (Exception ex)
            {
                result = QuickApplyResult.Of(QuickApplyOutcome.ProfileUnreadable, profileId, QuickApply.SafeMessage(ex));
            }
            if (result.Outcome == QuickApplyOutcome.Applied && result.ProfileId != null)
            {
                ReloadProfiles();
                SelectProfileById(result.ProfileId);
                UpdateHeader();
                RefreshTrayMenu();
                AppendApplyReports(result.Reports);
            }
            return ReportQuickResult(result, label, fromHotkey);
        }
        finally
        {
            _quickApplyInProgress = false;
        }
    }

    private QuickApplyResult ReportQuickResult(QuickApplyResult r, string label, bool fromHotkey)
    {
        LastQuickApplyResult = r;
        _quickApplyCount++;
        int moved = r.Reports.Count(x => x.Result == PlaceResult.Moved);
        int skipped = r.Reports.Count - moved;
        string msg = r.Outcome switch
        {
            QuickApplyOutcome.Applied =>
                $"{label} 적용: {r.ProfileName} — 이동 {moved}, 건너뜀 {skipped} — 자동 배치 켜짐" +
                (r.Detail != null ? " (설정 저장 실패: 이번 실행 동안만 유지)" : ""),
            QuickApplyOutcome.Unassigned => $"{label}: 아직 배치가 지정되지 않았습니다. '단축키 설정…'에서 저장된 배치를 지정하세요.",
            QuickApplyOutcome.ProfileMissing => $"{label}: 지정된 배치를 찾을 수 없어(삭제됨) 아무것도 적용하지 않았습니다.",
            QuickApplyOutcome.ProfileUnreadable => $"{label}: 지정된 배치 파일을 읽을 수 없어 아무것도 적용하지 않았습니다.",
            QuickApplyOutcome.NoProfiles => $"{label}: 저장된 배치가 없습니다.",
            QuickApplyOutcome.Busy => $"{label}: 이전 적용이 아직 진행 중이라 무시했습니다.",
            _ => label
        };
        AppendStatus(msg);
        if (fromHotkey && !Visible && !_suppressModalDialogs)
        {
            try { _tray.ShowBalloonTip(2500, AppInfo.ProductName, msg, ToolTipIcon.Info); } catch { }
        }
        RefreshQuickBar();
        return r;
    }

    private void SelectProfileById(string id)
    {
        for (int i = 0; i < _profileList.Items.Count; i++)
        {
            if (((ProfileRow)_profileList.Items[i]!).Profile.Id == id)
            {
                _profileList.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Slot labels + tooltips. Unassigned / missing slots explain what to do.</summary>
    private void RefreshQuickBar()
    {
        if (_slotButtons[0] is null) return;
        var cfg = _hotkeyController?.Current ?? HotkeyConfig.CreateDefault();
        var statuses = _hotkeyController?.Statuses;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in _profilesCache) names[p.Id] = p.Name;

        string KeyLine(HotkeyActionId a)
        {
            var b = cfg.Get(a);
            string state = !b.Enabled
                ? "꺼짐 (버튼은 그대로 사용 가능)"
                : statuses != null && statuses.TryGetValue(a, out var s) ? s.KoreanText : "확인 전";
            return $"전역 단축키 {HotkeyRules.Format(b)}: {state}";
        }

        for (int i = 0; i < HotkeyActions.SlotCount; i++)
        {
            var action = (HotkeyActionId)i;
            var b = cfg.Get(action);
            int n = i + 1;
            string text, tip;
            if (b.ProfileId is null)
            {
                text = $"{n}  (비어 있음)";
                tip = $"슬롯 {n}: 아직 배치가 지정되지 않았습니다.\n'단축키 설정…'에서 저장된 배치를 지정하세요.\n{KeyLine(action)}";
            }
            else if (names.TryGetValue(b.ProfileId, out var nm))
            {
                text = $"{n}  {nm}";
                tip = $"슬롯 {n}: {nm}\n누르면 이 배치를 바로 적용합니다 (이미 열린 창만 이동, 자동 배치 켜짐).\n{KeyLine(action)}";
            }
            else
            {
                text = $"{n}  (찾을 수 없음)";
                tip = $"슬롯 {n}: 지정된 배치가 삭제되었거나 읽을 수 없습니다. 눌러도 아무것도 적용하지 않습니다.\n'단축키 설정…'에서 다시 지정하세요.\n{KeyLine(action)}";
            }
            _slotButtons[i].Text = text;
            _slotButtons[i].AccessibleName = $"슬롯 {n}: " + text[(n.ToString().Length)..].Trim();
            _quickTips.SetToolTip(_slotButtons[i], tip);
        }
        _quickTips.SetToolTip(_prevBtn, $"저장 목록 순서상 이전 배치를 바로 적용합니다 (처음에서 끝으로 순환).\n{KeyLine(HotkeyActionId.Previous)}");
        _quickTips.SetToolTip(_nextBtn, $"저장 목록 순서상 다음 배치를 바로 적용합니다 (끝에서 처음으로 순환).\n{KeyLine(HotkeyActionId.Next)}");
        _quickTips.SetToolTip(_hotkeySettingsBtn, "슬롯 1~8에 배치를 지정하고, 슬롯과 이전/다음의 전역 단축키를 켜거나 바꿉니다.");
    }

    private void DoHotkeySettings()
    {
        if (_hotkeyController is null)
        {
            NotifyError("전역 단축키 기능을 초기화하지 못했습니다. 프로그램을 다시 시작해 보세요.");
            return;
        }
        ReloadProfiles();
        using var dlg = new HotkeySettingsDialog(_hotkeyController.Current, ProfileOrdering.Sort(_profilesCache), _hotkeyController.Statuses);
        DialogResult dr;
        _hotkeyDialogOpen = true;
        _openHotkeyDialog = dlg;
        try { dr = dlg.ShowDialog(this); }
        finally
        {
            _hotkeyDialogOpen = false;
            _openHotkeyDialog = null;
        }
        if (dr != DialogResult.OK || dlg.ResultConfig is null)
        {
            AppendStatus("단축키 설정 변경을 취소했습니다. 이전 설정을 그대로 유지합니다.");
            return;
        }
        var res = PerformHotkeyCommit(dlg.ResultConfig);
        if (res.Outcome != HotkeyCommitOutcome.Applied)
        {
            NotifyError(string.Join("\n", res.Errors) + "\n\n이전 단축키 설정과 등록 상태를 그대로 유지합니다.");
            return;
        }
        var failed = res.Statuses.Where(kv => kv.Value.State == HotkeyRegState.Failed).ToList();
        if (failed.Count > 0)
        {
            NotifyInfo("설정은 저장했지만 다음 단축키는 등록하지 못했습니다:\n" +
                       string.Join("\n", failed.Select(kv => $"- {HotkeyActions.KoreanLabel(kv.Key)}: {kv.Value.KoreanText}")) +
                       "\n\n다른 조합으로 바꾸거나 해당 프로그램을 종료한 뒤 다시 저장하세요. 버튼은 계속 사용할 수 있습니다.");
        }
    }

    /// <summary>
    /// Saves the proposal first, then re-registers only changed keys. Invalid or failed save keeps
    /// the previous in-memory config and every existing registration. Never applies a layout.
    /// </summary>
    public HotkeyCommitResult PerformHotkeyCommit(HotkeyConfig proposed)
    {
        HotkeyCommitResult res;
        if (_hotkeyController is null)
        {
            res = new HotkeyCommitResult(HotkeyCommitOutcome.SaveFailed,
                new[] { "전역 단축키 기능이 초기화되지 않았습니다." },
                new Dictionary<HotkeyActionId, HotkeyStatus>());
        }
        else
        {
            res = _hotkeyController.Commit(proposed);
        }
        LastHotkeyCommitResult = res;
        if (res.Outcome == HotkeyCommitOutcome.Applied)
        {
            int on = res.Statuses.Count(kv => kv.Value.State == HotkeyRegState.Registered);
            int fail = res.Statuses.Count(kv => kv.Value.State == HotkeyRegState.Failed);
            AppendStatus($"단축키 설정 저장됨: 등록된 단축키 {on}개" + (fail > 0 ? $", 등록 실패 {fail}개" : ""));
        }
        else
        {
            AppendStatus("단축키 설정을 적용하지 않았습니다: " + string.Join(" / ", res.Errors));
        }
        RefreshQuickBar();
        return res;
    }

    private void TogglePauseResume()
    {
        if (_monitor.IsMonitoring) _monitor.Pause();
        else _monitor.Resume();
        UpdateHeader();
        AppendStatus(_monitor.IsMonitoring ? "자동 배치 재개" : "자동 배치 일시정지");
    }

    private void HideToTray()
    {
        Hide();
        _tray.ShowBalloonTip(1500, AppInfo.ProductName, "트레이에서 계속 실행 중입니다.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        // In tray-start mode we started with ShowInTaskbar=false; restore it so an actually-visible
        // form appears in the taskbar as the user expects. Already true for the normal-GUI path.
        if (!ShowInTaskbar) ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    /// <summary>
    /// Tray "단축키 설정…": shows the main window, then opens the same settings dialog as the main
    /// button. Opens nothing new if the dialog (or any other modal) is already open. Never applies a
    /// layout, never assigns slots or enables keys by itself.
    /// </summary>
    private void OpenHotkeySettingsFromTray()
    {
        ShowFromTray();
        if (_hotkeyDialogOpen)
        {
            try { _openHotkeyDialog?.Activate(); } catch { }
            return;
        }
        if (IsExplicitApplyBlocked(out var why))
        {
            AppendStatus($"단축키 설정을 열 수 없습니다: {why}. 열린 창을 닫은 뒤 다시 시도하세요.");
            return;
        }
        // Deferred so the tray menu has closed before the modal dialog starts.
        BeginInvoke(new Action(DoHotkeySettings));
    }

    /// <summary>
    /// Called from a thread-pool thread when a second normal launch signals this instance. Only
    /// marshals to the UI thread. A request that arrives before the form is ready (no handle yet /
    /// Bootstrap not finished — the listener is created before Application.Run) is QUEUED and
    /// delivered by Bootstrap, so it is never lost. Returns false only when the form is closing or
    /// disposed.
    /// </summary>
    public bool RequestExternalActivation()
    {
        if (IsDisposed || _reallyExit) return false;
        if (!_activationReady)
        {
            Interlocked.Exchange(ref _pendingExternalActivation, 1);
            // Handshake with Bootstrap: if it became ready meanwhile, exactly one side drains.
            if (_activationReady && Interlocked.Exchange(ref _pendingExternalActivation, 0) == 1)
                return PostExternalActivation();
            return true;
        }
        return PostExternalActivation();
    }

    private bool PostExternalActivation()
    {
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || _reallyExit) return;
                _externalActivationCount++;
                ShowFromTray();
            }));
            return true;
        }
        catch (InvalidOperationException) { return false; } // includes ObjectDisposedException
    }

    /// <summary>UI thread, end of Bootstrap: accept activations and deliver one queued request.</summary>
    private void MarkActivationReady()
    {
        _activationReady = true;
        if (Interlocked.Exchange(ref _pendingExternalActivation, 0) == 1)
        {
            // Posted (not called) so in tray-start mode it runs after the suppressed first show.
            PostExternalActivation();
        }
    }

    /// <summary>Set by Program when the activation listener could not be created (reported in status).</summary>
    public void SetActivationListenerError(string reason) => _activationListenerError = reason;

    private void ExitFromTray()
    {
        _reallyExit = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _timer.Stop();
        ShutdownHotkeys();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIconOwned)
        {
            try { _trayIcon.Dispose(); } catch { }
        }
    }

    public void AppendStatus(string s)
    {
        _statusLabel.Text = $"{DateTime.Now:HH:mm:ss} {s}";
        // The header column ellipsizes long messages; the full text stays available.
        _quickTips.SetToolTip(_statusLabel, _statusLabel.Text);
        _statusLabel.AccessibleDescription = _statusLabel.Text;
    }

    /// <summary>Shows an informational dialog or appends to status when modal dialogs are suppressed
    /// (e.g. during the UI smoke harness so no dialog can block the test timer).</summary>
    private void NotifyInfo(string msg)
    {
        if (_suppressModalDialogs) { AppendStatus(msg); return; }
        MessageBox.Show(this, msg, "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void NotifyError(string msg)
    {
        if (_suppressModalDialogs) { AppendStatus(msg); return; }
        MessageBox.Show(this, msg, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void AppendApplyReports(IReadOnlyList<PlaceReport> reports)
    {
        _resultList.BeginUpdate();
        foreach (var r in reports)
        {
            var li = new ListViewItem(new[]
            {
                DateTime.Now.ToString("HH:mm:ss"),
                string.IsNullOrEmpty(r.EntryRole) ? "(미지정)" : r.EntryRole,
                KoreanOf(r.Result),
                DescribeReport(r)
            });
            _resultList.Items.Insert(0, li);
        }
        TrimResultList();
        _resultList.EndUpdate();
    }

    private void AppendAutoReports(IReadOnlyList<PlaceReport> reports)
    {
        AppendApplyReports(reports);
    }

    private void TrimResultList()
    {
        while (_resultList.Items.Count > 200) _resultList.Items.RemoveAt(_resultList.Items.Count - 1);
    }

    private static string KoreanOf(PlaceResult r) => r switch
    {
        PlaceResult.Moved => "이동됨",
        PlaceResult.Skipped_NoMatch => "해당 창 없음",
        PlaceResult.Skipped_Ambiguous => "구분 불가",
        PlaceResult.Skipped_SlotOccupied => "자리 사용 중",
        PlaceResult.Skipped_NativeFailed => "API 실패",
        PlaceResult.Skipped_IdentityStale => "식별 불일치",
        PlaceResult.Skipped_PendingRetry => "안정화 포기",
        PlaceResult.Skipped_Rejected => "앱이 거부",
        _ => r.ToString()
    };

    private static string DescribeReport(PlaceReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(r.Note ?? "");
        if (r.Adjusted) sb.Append(sb.Length > 0 ? " · " : "").Append("화면 범위에 맞춰 조정됨");
        if (r.TargetRect.Width > 0)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"목표 {r.TargetRect.X},{r.TargetRect.Y} {r.TargetRect.Width}x{r.TargetRect.Height}");
        }
        return sb.ToString();
    }

    // --- UI smoke / test hooks ---
    public void UiSmokeTriggerHide() => HideToTray();
    public void UiSmokeTriggerShow() => ShowFromTray();
    public void UiSmokeTriggerPauseResume() => TogglePauseResume();
    public void UiSmokeTriggerExit() => ExitFromTray();
    public void UiSmokeForceRefreshTrayMenu() => RefreshTrayMenu();
    public void UiSmokeSelectProfileById(string id)
    {
        for (int i = 0; i < _profileList.Items.Count; i++)
        {
            if (((ProfileRow)_profileList.Items[i]!).Profile.Id == id)
            {
                _profileList.SelectedIndex = i;
                return;
            }
        }
    }
    public void UiSmokePerformApplyClick() => _applyBtn.PerformClick();
    public void UiSmokePerformUpdateClick() => _updateBtn.PerformClick();
    public void UiSmokePerformRenameClick() => _renameBtn.PerformClick();
    public void UiSmokePerformRemoveEntryClick() => _removeEntryBtn.PerformClick();
    public ListView UiSmokeEntryList => _entryList;
    public void UiSmokeInjectFailingStore()
    {
        _profiles = new UiSmokeAlwaysFailingProfileStore(_profilesOriginal.ProfilesDir);
    }
    public void UiSmokeRestoreOriginalStore() => _profiles = _profilesOriginal;
    public Profile? UiSmokeSelectedProfile() => _selectedProfile;
    public Profile? UiSmokeActiveProfile() => _monitor.ActiveProfile;
    public void UiSmokeSuppressDialogs(bool suppress) => _suppressModalDialogs = suppress;
    public bool UiSmokeTrayVisible => _tray.Visible;
    public bool UiSmokeTimerEnabled => _timer.Enabled;

    // --- v0.2.0 quick apply / hotkey QA hooks ---
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public QuickApplyResult? LastQuickApplyResult { get; private set; }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public HotkeyCommitResult? LastHotkeyCommitResult { get; private set; }

    public HotkeyConfig? UiSmokeHotkeyConfig => _hotkeyController?.Current;
    public IReadOnlyDictionary<HotkeyActionId, HotkeyStatus>? UiSmokeHotkeyStatuses => _hotkeyController?.Statuses;
    public string? UiSmokeHotkeyLoadError => _hotkeyController?.LoadError;
    /// <summary>Number of WM_HOTKEY messages accepted (registered id, not ignored).</summary>
    public int UiSmokeHotkeyInvocationCount => _hotkeyInvocationCount;
    /// <summary>Number of quick-apply results reported (buttons + hotkeys + navigation).</summary>
    public int UiSmokeQuickApplyCount => _quickApplyCount;
    /// <summary>SYNTHETIC only: posts WM_HOTKEY to our own message window (no OS keyboard routing).</summary>
    public bool UiSmokePostSyntheticHotkey(HotkeyActionId action) =>
        _hotkeyWindow?.PostSyntheticHotkey(HotkeyActions.RegistrationId(action)) ?? false;
    public string UiSmokeSlotButtonText(int slotNumber) => _slotButtons[slotNumber - 1].Text;
    public void UiSmokeClickSlot(int slotNumber) => _slotButtons[slotNumber - 1].PerformClick();
    public void UiSmokeClickPrevious() => _prevBtn.PerformClick();
    public void UiSmokeClickNext() => _nextBtn.PerformClick();
    public IReadOnlyList<Button> UiSmokeQuickButtons =>
        _slotButtons.Concat(new[] { _prevBtn, _nextBtn, _hotkeySettingsBtn }).ToList();

    /// <summary>v0.3.2 layout groups whose buttons are meant to be identical in size.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Button>> UiSmokeActionGroups => new Dictionary<string, IReadOnlyList<Button>>
    {
        ["slots"] = _slotButtons.ToList(),
        ["nav"] = new[] { _prevBtn, _nextBtn, _hotkeySettingsBtn },
        ["sidebar"] = new[] { _newBtn, _updateBtn, _activateBtn, _renameBtn, _deleteBtn },
        ["entry"] = new[] { _editEntryBtn, _removeEntryBtn },
        ["footer"] = new[] { _applyBtn, _pauseBtn, _hideBtn },
    };
    public IReadOnlyList<string> UiSmokeProfileListIds =>
        _profileList.Items.Cast<ProfileRow>().Select(r => r.Profile.Id).ToList();

    // --- v0.2.0 modal-reentrancy QA hooks (owned modals only; never touch other processes) ---
    public int UiSmokeHotkeySuppressedCount => _hotkeySuppressedCount;
    public int UiSmokeHotkeyForwardedCount => _hotkeyForwardedCount;
    public HotkeySettingsDialog? UiSmokeOpenHotkeyDialog => _openHotkeyDialog;
    /// <summary>Native owner-enabled state (MessageBox/ShowDialog disable the owner).</summary>
    public bool UiSmokeOwnerEnabledNative => IsHandleCreated && NativeMethods.IsWindowEnabled(Handle);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool UiSmokeModalOpen { get; private set; }

    private Form? _uiSmokeModal;

    /// <summary>BLOCKING (call through BeginInvoke): owned WinForms modal on this form.</summary>
    public void UiSmokeShowOwnedModalForm()
    {
        using var f = new Form
        {
            Text = "WindowDock QA modal",
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(320, 160)
        };
        _uiSmokeModal = f;
        f.Shown += (_, _) => UiSmokeModalOpen = true;
        try { f.ShowDialog(this); }
        finally
        {
            UiSmokeModalOpen = false;
            _uiSmokeModal = null;
        }
    }

    public void UiSmokeCloseOwnedModalForm() => _uiSmokeModal?.Close();

    /// <summary>BLOCKING (call through BeginInvoke): native Win32 MessageBox owned by this form.</summary>
    public void UiSmokeShowNativeMessageBox()
    {
        UiSmokeModalOpen = true;
        try { MessageBox.Show(this, "창자리 QA 확인 창 (자동으로 닫힘)", "QA", MessageBoxButtons.OK); }
        finally { UiSmokeModalOpen = false; }
    }

    /// <summary>Closes only #32770 dialogs on THIS UI thread whose owner is this form.</summary>
    public int UiSmokeCloseOwnedNativeDialogs()
    {
        if (!IsHandleCreated) return 0;
        var owner = Handle;
        var found = new List<IntPtr>();
        NativeMethods.EnumThreadWindows(NativeMethods.GetCurrentThreadId(), (h, _) =>
        {
            if (NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) == owner && WindowEnumerator.ReadClassName(h) == "#32770")
                found.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var h in found) NativeMethods.PostMessageW(h, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return found.Count;
    }

    /// <summary>BLOCKING (call through BeginInvoke): the real hotkey settings dialog.</summary>
    public void UiSmokeOpenHotkeySettings() => DoHotkeySettings();

    // --- v0.2.1 identity / activation QA hooks ---
    public int UiSmokeExternalActivationCount => _externalActivationCount;
    /// <summary>True when the form uses the embedded WindowDock icon (not the generic fallback).</summary>
    public bool UiSmokeFormIconIsEmbedded => AppIcon.IsLoaded && ReferenceEquals(Icon, AppIcon.Icon);
    /// <summary>True when the tray uses an icon derived from the embedded WindowDock icon.</summary>
    public bool UiSmokeTrayIconIsEmbedded => AppIcon.IsLoaded && _trayIconOwned && ReferenceEquals(_tray.Icon, _trayIcon);

    private sealed class ProfileRow
    {
        public Profile Profile { get; }
        public bool IsActive { get; }
        public ProfileRow(Profile p, bool active) { Profile = p; IsActive = active; }
        public override string ToString() => IsActive ? $"● {Profile.Name}" : "  " + Profile.Name;
    }
}

/// <summary>Test-only ProfileStore that always throws on Save. Used by UI smoke to prove the
/// transactional PerformUpdateFromCurrent leaves the live model untouched on save failure.</summary>
internal sealed class UiSmokeAlwaysFailingProfileStore : ProfileStore
{
    public UiSmokeAlwaysFailingProfileStore(string profilesDir) : base(Path.GetDirectoryName(profilesDir) ?? profilesDir) { }
    public override void Save(Profile p) => throw new InvalidOperationException("ui_smoke_forced_save_failure");
}

public sealed class InputDialog : Form
{
    private readonly TextBox _box;
    public string Value => _box.Text.Trim();
    public InputDialog(string title, string prompt, string initial)
    {
        SuspendLayout();
        UiStyle.ApplyForm(this);
        Text = title;
        Icon = AppIcon.ForForm; // shared; not owned/disposed
        ClientSize = new Size(440, 152);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        int pad = UiStyle.OuterPadding, half = UiStyle.HalfGap;
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(pad - half, pad - half, pad - half, 0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.RowHeight));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        body.Controls.Add(UiStyle.CellLabel(prompt), 0, 0);
        _box = UiStyle.CellInput(new TextBox { Text = initial, AccessibleName = prompt.TrimEnd(':', ' ') });
        body.Controls.Add(_box, 0, 1);

        var ok = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.OK }, "확인");
        var cancel = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.Cancel }, "취소");
        var footer = UiStyle.Footer(Array.Empty<(Button, int)>(),
            new (Button, int)[] { (ok, UiStyle.PrimaryButtonWidth), (cancel, UiStyle.PrimaryButtonWidth) });

        Controls.Add(body);
        Controls.Add(footer);
        AcceptButton = ok; CancelButton = cancel;
        ResumeLayout(true);
    }

    /// <summary>QA: the text box and the two footer buttons.</summary>
    public IReadOnlyList<Control> UiSmokeKeyControls =>
        new Control[] { _box }.Concat(Controls.OfType<TableLayoutPanel>().SelectMany(t => t.Controls.OfType<Button>())).ToList();
}
