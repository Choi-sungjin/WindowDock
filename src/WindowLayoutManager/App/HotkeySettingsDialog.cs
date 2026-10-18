using System.ComponentModel;
using WindowLayoutManager.Model;
using WindowLayoutManager.Native;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.App;

/// <summary>
/// Korean dialog for the eight quick-apply slots and the Previous/Next actions. Works on a
/// detached copy: Cancel/Escape changes nothing. OK validates the whole proposal (duplicate
/// enabled combinations, forbidden keys) and keeps the dialog open with a visible reason when
/// invalid. The caller commits the result (save first, then registration).
/// Fully keyboard operable: Tab/Shift+Tab move between fields, Enter = 확인, Esc = 취소; inside a
/// shortcut field, pressing a combination records it.
/// </summary>
public sealed class HotkeySettingsDialog : Form
{
    private readonly HotkeyConfig _initial;
    private readonly ComboBox[] _profileCombos = new ComboBox[HotkeyActions.SlotCount];
    private readonly CheckBox[] _enabled = new CheckBox[10];
    private readonly HotkeyCaptureBox[] _captures = new HotkeyCaptureBox[10];
    private readonly Label[] _statusLabels = new Label[10];
    private readonly Label _errorLabel = new();
    private readonly Button _ok;
    private readonly Button _cancel;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyConfig? ResultConfig { get; private set; }

    public HotkeySettingsDialog(HotkeyConfig current, IReadOnlyList<Profile> sortedProfiles, IReadOnlyDictionary<HotkeyActionId, HotkeyStatus> statuses)
    {
        _initial = current.Clone();
        Text = "빠른 적용 슬롯과 단축키";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(880, 640);
        MinimumSize = new Size(740, 600);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            AutoSize = false,
            Padding = new Padding(12, 10, 12, 4),
            Text = "슬롯 1~8에 저장된 배치를 지정하면 메인 창의 숫자 버튼으로 바로 적용할 수 있습니다 (단축키를 켜지 않아도 버튼은 동작).\n" +
                   "전역 단축키는 처음에는 모두 꺼져 있습니다. '사용'을 체크하면 창자리가 실행 중일 때(트레이 포함) 어디서든 동작합니다.\n" +
                   "단축키 칸을 선택하고 원하는 조합을 누르세요. Ctrl 또는 Alt가 필요하며 Windows 키와 F12는 쓸 수 없습니다."
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 12,
            AutoScroll = true,
            Padding = new Padding(12, 4, 12, 4),
            TabIndex = 0
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        for (int i = 0; i < 10; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        // Empty filler row absorbs the remaining height. Without it TableLayoutPanel stretches the
        // LAST real row (Next), where labels/checkbox center vertically but the single-line key
        // field stays at the top, so the row looks misaligned.
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        table.Controls.Add(HeaderLabel("항목"), 0, 0);
        table.Controls.Add(HeaderLabel("적용할 배치"), 1, 0);
        table.Controls.Add(HeaderLabel("단축키"), 2, 0);
        table.Controls.Add(HeaderLabel("키 조합 (칸 선택 후 누르기)"), 3, 0);
        table.Controls.Add(HeaderLabel("현재 상태"), 4, 0);

        var dupNames = sortedProfiles.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        foreach (var action in HotkeyActions.All)
        {
            int i = (int)action;
            int row = i + 1;
            var b = _initial.Get(action);
            var label = HotkeyActions.KoreanLabel(action);

            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);

            if (HotkeyActions.IsSlot(action))
            {
                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Dock = DockStyle.Fill,
                    AccessibleName = $"{label} 적용할 배치",
                    Margin = new Padding(3, 5, 3, 3)
                };
                combo.Items.Add(new ProfileChoice(null, "(할당 안 함)"));
                ProfileChoice? selected = null;
                foreach (var p in sortedProfiles)
                {
                    var text = dupNames.Contains(p.Name) ? $"{p.Name} ({ShortId(p.Id)})" : p.Name;
                    var choice = new ProfileChoice(p.Id, text);
                    combo.Items.Add(choice);
                    if (b.ProfileId == p.Id) selected = choice;
                }
                if (b.ProfileId != null && selected is null)
                {
                    // Stored ID no longer exists (deleted/corrupt). Keep it visible so the user can
                    // see and clear it instead of silently losing or remapping the binding.
                    selected = new ProfileChoice(b.ProfileId, "(찾을 수 없는 배치 — 다시 지정하세요)");
                    combo.Items.Add(selected);
                }
                combo.SelectedItem = selected ?? combo.Items[0];
                combo.SelectedIndexChanged += (_, _) => MarkChanged(i);
                _profileCombos[i] = combo;
                table.Controls.Add(combo, 1, row);
            }
            else
            {
                table.Controls.Add(new Label
                {
                    Text = action == HotkeyActionId.Previous
                        ? "저장 목록 순서상 이전 배치 적용 (처음에서 끝으로 순환)"
                        : "저장 목록 순서상 다음 배치 적용 (끝에서 처음으로 순환)",
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                }, 1, row);
            }

            var chk = new CheckBox
            {
                Text = "사용",
                Checked = b.Enabled,
                Dock = DockStyle.Fill,
                AccessibleName = $"{label} 전역 단축키 사용"
            };
            chk.CheckedChanged += (_, _) => MarkChanged(i);
            _enabled[i] = chk;
            table.Controls.Add(chk, 2, row);

            var cap = new HotkeyCaptureBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = $"{label} 단축키 키 조합. 이 칸에서 원하는 조합을 누르세요",
                Margin = new Padding(3, 5, 3, 3)
            };
            cap.SetCombo(b.Ctrl, b.Alt, b.Shift, b.Key);
            cap.Feedback += msg =>
            {
                _errorLabel.Text = msg is null ? "" : $"{label}: {msg}";
            };
            cap.ComboChanged += (_, _) => MarkChanged(i);
            _captures[i] = cap;
            table.Controls.Add(cap, 3, row);

            var st = new Label
            {
                Text = statuses.TryGetValue(action, out var s) ? s.KoreanText : "꺼짐",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            _statusLabels[i] = st;
            table.Controls.Add(st, 4, row);
        }

        _errorLabel.Dock = DockStyle.Bottom;
        _errorLabel.Height = 40;
        _errorLabel.AutoSize = false;
        _errorLabel.ForeColor = Color.Firebrick;
        _errorLabel.Padding = new Padding(12, 2, 12, 2);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8)
        };
        _cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 96, Height = 30 };
        _ok = new Button { Text = "확인", Width = 96, Height = 30 };
        _ok.Click += OnOk;
        var reset = new Button { Text = "기본 키 조합으로", Width = 130, Height = 30 };
        reset.Click += (_, _) => ResetCombos();
        bottom.Controls.Add(_cancel);
        bottom.Controls.Add(_ok);
        bottom.Controls.Add(reset);

        Controls.Add(table);
        Controls.Add(_errorLabel);
        Controls.Add(bottom);
        Controls.Add(intro);

        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    private static Label HeaderLabel(string t) => new()
    {
        Text = t,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray
    };

    private static string ShortId(string id) => id.Length <= 6 ? id : id[..6];

    private void MarkChanged(int index)
    {
        _statusLabels[index].Text = "변경됨 — 확인을 누르면 적용";
    }

    private void ResetCombos()
    {
        foreach (var action in HotkeyActions.All)
        {
            int i = (int)action;
            var d = HotkeyConfig.DefaultFor(action);
            if (!_captures[i].Matches(d.Ctrl, d.Alt, d.Shift, d.Key))
            {
                _captures[i].SetCombo(d.Ctrl, d.Alt, d.Shift, d.Key);
                MarkChanged(i);
            }
        }
        _errorLabel.Text = "키 조합을 기본값(Ctrl+Alt+1~8, Ctrl+Alt+←/→)으로 되돌렸습니다. 사용 여부와 배치 지정은 그대로입니다.";
    }

    /// <summary>Builds the proposal from the controls (does not touch the input config).</summary>
    public HotkeyConfig BuildProposal()
    {
        var cfg = _initial.Clone();
        foreach (var action in HotkeyActions.All)
        {
            int i = (int)action;
            var b = cfg.Get(action);
            b.Enabled = _enabled[i].Checked;
            b.Ctrl = _captures[i].Ctrl;
            b.Alt = _captures[i].Alt;
            b.Shift = _captures[i].Shift;
            b.Key = _captures[i].Key;
            if (HotkeyActions.IsSlot(action))
            {
                b.ProfileId = (_profileCombos[i].SelectedItem as ProfileChoice)?.Id;
            }
        }
        return cfg;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var cfg = BuildProposal();
        var errs = cfg.Validate();
        if (errs.Count > 0)
        {
            _errorLabel.Text = string.Join("\n", errs.Take(2));
            DialogResult = DialogResult.None;
            return;
        }
        ResultConfig = cfg;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Called by MainForm when one of OUR registered global hotkeys fires while this dialog is
    /// open. RegisterHotKey consumes that key press before the focused field sees it, so it is
    /// forwarded to the focused capture field (same validation as a direct key press). Returns
    /// false when no capture field has focus; the caller then just drops the command.
    /// Registrations are never released for this, so Cancel / save failure keep them unchanged.
    /// </summary>
    public bool TryForwardRegisteredCombo(bool ctrl, bool alt, bool shift, Keys key)
    {
        if (IsDisposed || !Visible) return false;
        if (ActiveControl is HotkeyCaptureBox cap && cap.Enabled)
        {
            cap.CaptureCombo(ctrl, alt, shift, key);
            return true;
        }
        return false;
    }

    // --- QA hooks ---
    public void UiSmokeFocusCapture(int index) => ActiveControl = _captures[index];
    public IReadOnlyList<HotkeyCaptureBox> UiSmokeCaptureBoxes => _captures;
    public IReadOnlyList<CheckBox> UiSmokeEnableBoxes => _enabled;
    public IReadOnlyList<ComboBox> UiSmokeProfileCombos => _profileCombos;
    public Button UiSmokeOkButton => _ok;
    public Button UiSmokeCancelButton => _cancel;

    private sealed class ProfileChoice
    {
        public string? Id { get; }
        public string Text { get; }
        public ProfileChoice(string? id, string text) { Id = id; Text = text; }
        public override string ToString() => Text;
    }
}

/// <summary>
/// Read-only text field that records a key combination when pressed while focused. Tab, Esc and
/// Enter keep their normal dialog meaning so the dialog stays keyboard navigable.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Ctrl { get; private set; }
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Alt { get; private set; }
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Shift { get; private set; }
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Keys Key { get; private set; }

    /// <summary>null = accepted; otherwise a Korean reason the key press was rejected.</summary>
    public event Action<string?>? Feedback;
    public event EventHandler? ComboChanged;

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        BackColor = SystemColors.Window;
        TextAlign = HorizontalAlignment.Center;
        TabStop = true;
    }

    public void SetCombo(bool ctrl, bool alt, bool shift, Keys key)
    {
        Ctrl = ctrl; Alt = alt; Shift = shift; Key = key;
        Text = HotkeyRules.Format(ctrl, alt, shift, key);
    }

    public bool Matches(bool ctrl, bool alt, bool shift, Keys key) =>
        Ctrl == ctrl && Alt == alt && Shift == shift && Key == key;

    protected override bool IsInputKey(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code == Keys.Tab || code == Keys.Escape || code == Keys.Enter) return false;
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        var code = e.KeyCode;
        if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey
            or Keys.LMenu or Keys.RMenu)
        {
            return; // wait for the non-modifier key
        }
        bool winDown = (NativeMethods.GetKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0
                       || (NativeMethods.GetKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;
        if (winDown || code is Keys.LWin or Keys.RWin)
        {
            Feedback?.Invoke("Windows 키 조합은 사용할 수 없습니다.");
            return;
        }
        CaptureCombo(e.Control, e.Alt, e.Shift, code);
    }

    /// <summary>Validates and records a combination (direct key press or forwarded hotkey).
    /// Returns true when accepted.</summary>
    public bool CaptureCombo(bool ctrl, bool alt, bool shift, Keys key)
    {
        var err = HotkeyRules.ValidateCombo(ctrl, alt, shift, key);
        if (err != null)
        {
            Feedback?.Invoke(err);
            return false;
        }
        SetCombo(ctrl, alt, shift, key);
        Feedback?.Invoke(null);
        ComboChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        e.Handled = true;
    }
}
