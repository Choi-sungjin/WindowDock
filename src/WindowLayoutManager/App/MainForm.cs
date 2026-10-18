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
    public ToolStripMenuItem TrayExitItem { get; }

    private List<Profile> _profilesCache = new();
    private Profile? _selectedProfile;
    private bool _reallyExit = false;
    private bool _suppressModalDialogs = false;
    private readonly bool _startInTray;
    private bool _bootstrapped;
    private bool _firstShowConsumed;

    public MainForm(ProfileStore profiles, SettingsStore settings, MonitoringService monitor, IWindowProvider provider)
        : this(profiles, settings, monitor, provider, startInTray: false) { }

    public MainForm(ProfileStore profiles, SettingsStore settings, MonitoringService monitor, IWindowProvider provider, bool startInTray)
    {
        _profiles = profiles;
        _profilesOriginal = profiles;
        _settings = settings;
        _monitor = monitor;
        _provider = provider;
        _startInTray = startInTray;

        Text = "창자리 (WindowDock)";
        Size = new Size(1180, 760);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        if (_startInTray)
        {
            // Form must never flash onto the taskbar at startup; the user will Open it from the
            // tray if they want to see it. ShowInTaskbar flips back to true in ShowFromTray.
            ShowInTaskbar = false;
        }

        BuildLayout();

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "창자리 (WindowDock)",
            Icon = SystemIcons.Application
        };
        _trayMenu = new ContextMenuStrip();
        TrayOpenItem = new ToolStripMenuItem("열기");
        TrayOpenItem.Click += (_, _) => ShowFromTray();
        TrayApplyMenu = new ToolStripMenuItem("배치 적용");
        TrayPauseItem = new ToolStripMenuItem("자동 배치 일시정지");
        TrayPauseItem.Click += (_, _) => TogglePauseResume();
        TrayExitItem = new ToolStripMenuItem("종료");
        TrayExitItem.Click += (_, _) => ExitFromTray();
        _trayMenu.Items.Add(TrayOpenItem);
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
        RefreshTrayMenu();
        UpdateHeader();
        UpdateEmptyState();
        _timer.Start();
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

    private void BuildLayout()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 3, Padding = new Padding(12, 8, 12, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        _activeLabel.AutoSize = false; _activeLabel.Dock = DockStyle.Fill; _activeLabel.TextAlign = ContentAlignment.MiddleLeft; _activeLabel.Font = new Font(Font, FontStyle.Bold);
        _monitorLabel.AutoSize = false; _monitorLabel.Dock = DockStyle.Fill; _monitorLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoSize = false; _statusLabel.Dock = DockStyle.Fill; _statusLabel.TextAlign = ContentAlignment.MiddleLeft; _statusLabel.ForeColor = Color.DimGray;
        header.Controls.Add(_activeLabel, 0, 0);
        header.Controls.Add(_monitorLabel, 1, 0);
        header.Controls.Add(_statusLabel, 2, 0);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10, 10, 10, 10) };
        _applyBtn.Text = "지금 적용 + 자동 배치 시작"; _applyBtn.Width = 220; _applyBtn.Height = 32;
        _pauseBtn.Text = "자동 배치 시작"; _pauseBtn.Width = 160; _pauseBtn.Height = 32;
        _hideBtn.Text = "트레이로 숨기기"; _hideBtn.Width = 160; _hideBtn.Height = 32;
        _applyBtn.Click += (_, _) => DoApply();
        _pauseBtn.Click += (_, _) => TogglePauseResume();
        _hideBtn.Click += (_, _) => HideToTray();
        bottom.Controls.Add(_applyBtn);
        bottom.Controls.Add(_pauseBtn);
        bottom.Controls.Add(_hideBtn);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.FixedPanel = FixedPanel.Panel1;

        var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));

        _profileList.Dock = DockStyle.Fill;
        _profileList.SelectedIndexChanged += (_, _) => OnProfileSelected();
        leftLayout.Controls.Add(_profileList, 0, 0);

        var profButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(4, 4, 4, 4) };
        _newBtn.Text = "현재 창에서 새로 저장"; _newBtn.Width = 170; _newBtn.Height = 28;
        _updateBtn.Text = "선택 배치 업데이트"; _updateBtn.Width = 160; _updateBtn.Height = 28;
        _activateBtn.Text = "활성 배치로 선택"; _activateBtn.Width = 140; _activateBtn.Height = 28;
        _renameBtn.Text = "이름 변경"; _renameBtn.Width = 90; _renameBtn.Height = 28;
        _deleteBtn.Text = "삭제"; _deleteBtn.Width = 70; _deleteBtn.Height = 28;
        _newBtn.Click += (_, _) => DoCreateFromCurrent();
        _updateBtn.Click += (_, _) => DoUpdateFromCurrent();
        _activateBtn.Click += (_, _) => DoActivateSelected();
        _renameBtn.Click += (_, _) => DoRename();
        _deleteBtn.Click += (_, _) => DoDelete();
        profButtons.Controls.Add(_newBtn);
        profButtons.Controls.Add(_updateBtn);
        profButtons.Controls.Add(_activateBtn);
        profButtons.Controls.Add(_renameBtn);
        profButtons.Controls.Add(_deleteBtn);
        leftLayout.Controls.Add(profButtons, 0, 1);

        split.Panel1.Controls.Add(leftLayout);

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        rightSplit.FixedPanel = FixedPanel.None;

        var entryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        entryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));

        _entryList.Dock = DockStyle.Fill;
        _entryList.View = View.Details;
        _entryList.FullRowSelect = true;
        _entryList.GridLines = true;
        _entryList.Columns.Add("역할", 150);
        _entryList.Columns.Add("프로그램", 130);
        _entryList.Columns.Add("타이틀 포함", 160);
        _entryList.Columns.Add("배치 위치/크기", 180);
        _entryList.Columns.Add("상태", 90);
        _entryList.MultiSelect = false;
        _entryList.DoubleClick += (_, _) => DoEditEntry();
        entryLayout.Controls.Add(_entryList, 0, 0);

        var entryBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        _editEntryBtn.Text = "항목 편집(세부)"; _editEntryBtn.Width = 130; _editEntryBtn.Height = 28;
        _removeEntryBtn.Text = "항목 삭제"; _removeEntryBtn.Width = 110; _removeEntryBtn.Height = 28;
        _editEntryBtn.Click += (_, _) => DoEditEntry();
        _removeEntryBtn.Click += (_, _) => DoRemoveEntry();
        entryBtns.Controls.Add(_editEntryBtn);
        entryBtns.Controls.Add(_removeEntryBtn);
        entryLayout.Controls.Add(entryBtns, 0, 1);

        var resultWrap = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        resultWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        resultWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var resultTitle = new Label { Text = "최근 적용/자동 배치 결과", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray };
        resultWrap.Controls.Add(resultTitle, 0, 0);
        _resultList.Dock = DockStyle.Fill;
        _resultList.View = View.Details;
        _resultList.FullRowSelect = true;
        _resultList.GridLines = true;
        _resultList.Columns.Add("시간", 70);
        _resultList.Columns.Add("역할", 140);
        _resultList.Columns.Add("결과", 110);
        _resultList.Columns.Add("설명", 400);
        resultWrap.Controls.Add(_resultList, 0, 1);
        entryLayout.Controls.Add(resultWrap, 0, 2);

        _preview.Dock = DockStyle.Fill;

        var previewWrap = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        previewWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        previewWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _emptyHelpLabel.AutoSize = false;
        _emptyHelpLabel.Dock = DockStyle.Fill;
        _emptyHelpLabel.TextAlign = ContentAlignment.MiddleLeft;
        _emptyHelpLabel.ForeColor = Color.FromArgb(80, 80, 80);
        _emptyHelpLabel.Padding = new Padding(10, 4, 10, 4);
        _emptyHelpLabel.Text = "창을 원하는 자리에 배치 후 '현재 창에서 새로 저장'을 누르세요. 이후 '지금 적용'을 누르면 그 배치로 되돌리고, 같은 프로그램의 새 창을 자동으로 그 자리에 놓습니다.";
        previewWrap.Controls.Add(_emptyHelpLabel, 0, 0);
        previewWrap.Controls.Add(_preview, 0, 1);

        rightSplit.Panel1.Controls.Add(entryLayout);
        rightSplit.Panel2.Controls.Add(previewWrap);

        split.Panel2.Controls.Add(rightSplit);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(header);

        HandleCreated += (_, _) =>
        {
            try
            {
                split.Panel1MinSize = 240;
                split.Panel2MinSize = 420;
                if (split.Width > 680) split.SplitterDistance = 310;
                if (rightSplit.Height > 320) rightSplit.SplitterDistance = Math.Min(rightSplit.Height - 180, Math.Max(220, rightSplit.Height / 2));
            }
            catch { }
        };
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
        foreach (var p in _profilesCache.OrderBy(p => p.Name))
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
        _monitorLabel.Text = _monitor.IsMonitoring ? "자동 배치: 켜짐 ●" : "자동 배치: 꺼짐 ○";
        _monitorLabel.ForeColor = _monitor.IsMonitoring ? Color.ForestGreen : Color.DimGray;
        _pauseBtn.Text = _monitor.IsMonitoring ? "자동 배치 일시정지" : "자동 배치 시작";
        TrayPauseItem.Text = _monitor.IsMonitoring ? "자동 배치 일시정지" : "자동 배치 시작";
    }

    private void RefreshTrayMenu()
    {
        TrayApplyMenu.DropDownItems.Clear();
        foreach (var p in _profilesCache.OrderBy(p => p.Name))
        {
            var captured = p;
            var item = new ToolStripMenuItem(p.Name) { Tag = p };
            item.Click += (_, _) =>
            {
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

        var proposal = ProfileUpdater.Build(_selectedProfile, snap.Windows);
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
        ProfileUpdater.CopyInto(_selectedProfile, proposal.Proposed);
        if (_monitor.ActiveProfile?.Id == _selectedProfile.Id)
        {
            _monitor.RefreshActiveFromDisk();
        }
        ReloadProfiles();
        RefreshTrayMenu();
        AppendStatus($"업데이트: {_selectedProfile.Name} (변경 {proposal.UpdatedCount}, 보존 {proposal.PreservedNoMatchCount + proposal.PreservedAmbiguousCount})");
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
        using var dlg = new InputDialog("배치 이름 변경", "새 이름:", _selectedProfile.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
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
        if (MessageBox.Show(this, $"'{_selectedProfile.Name}' 배치를 삭제하시겠습니까?", "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _profiles.Delete(_selectedProfile.Id);
        if (_monitor.ActiveProfile?.Id == _selectedProfile.Id)
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
        using var dlg = new EntryEditDialog(entry);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
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
        if (MessageBox.Show(this, $"'{entry.Role}' 항목을 삭제하시겠습니까?", "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
        _tray.ShowBalloonTip(1500, "창자리 (WindowDock)", "트레이에서 계속 실행 중입니다.", ToolTipIcon.Info);
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
        _tray.Visible = false;
        _tray.Dispose();
    }

    public void AppendStatus(string s)
    {
        _statusLabel.Text = $"{DateTime.Now:HH:mm:ss} {s}";
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
        Text = title;
        Size = new Size(420, 180);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var lbl = new Label { Text = prompt, Location = new Point(14, 16), AutoSize = true };
        _box = new TextBox { Location = new Point(14, 44), Width = 380, Text = initial };
        var ok = new Button { Text = "확인", Location = new Point(214, 90), Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", Location = new Point(306, 90), Width = 90, DialogResult = DialogResult.Cancel };
        Controls.Add(lbl); Controls.Add(_box); Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}
