using WindowLayoutManager.Model;

namespace WindowLayoutManager.App;

public sealed class EntryEditDialog : Form
{
    private readonly ProfileEntry _entry;
    private readonly TextBox _role;
    private readonly TextBox _titleContains;
    private readonly NumericUpDown _x;
    private readonly NumericUpDown _y;
    private readonly NumericUpDown _w;
    private readonly NumericUpDown _h;
    private readonly CheckBox _max;

    // Non-mutating results. The caller reads these on DialogResult.OK and applies them
    // transactionally — the input ProfileEntry is NEVER modified by this dialog.
    public string RoleResult { get; private set; } = "";
    public string? TitleContainsResult { get; private set; }
    public SavedRect RectResult { get; private set; } = new();
    public bool MaximizedResult { get; private set; }

    /// <summary>Test hook: the four geometry input fields in X, Y, Width, Height order. The UI
    /// smoke uses this to verify all four are visible/enabled and fully inside the dialog bounds
    /// at both default and minimum size.</summary>
    public IReadOnlyList<NumericUpDown> UiSmokeRectFields => new[] { _x, _y, _w, _h };

    public EntryEditDialog(ProfileEntry entry)
    {
        _entry = entry;
        SuspendLayout();
        UiStyle.ApplyForm(this);
        Text = $"항목 편집 — {entry.Role}";
        Icon = AppIcon.ForForm; // shared process-lifetime icon; not owned/disposed by the dialog
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 460);
        Size = new Size(680, 470);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        int pad = UiStyle.OuterPadding, half = UiStyle.HalfGap;
        var p = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(pad - half, pad - half, pad - half, 0),
            AutoSize = false,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 4; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.RowHeight)); // role, exe, class, title
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));                                              // rect hint
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));                                              // X/Y/W/H captions
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.RowHeight));                               // X/Y/W/H inputs
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.RowHeight));                               // maximized
        p.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));                                             // filler (no stretched last row)

        p.Controls.Add(UiStyle.CellLabel("역할 이름"), 0, 0);
        _role = UiStyle.CellInput(new TextBox { Text = entry.Role, AccessibleName = "역할 이름" });
        p.Controls.Add(_role, 1, 0);

        // Long executable path / class: read-only text boxes so the full value can be selected,
        // scrolled with the caret and read by screen readers (no hidden ellipsis-only text).
        p.Controls.Add(UiStyle.CellLabel("실행 경로"), 0, 1);
        p.Controls.Add(UiStyle.CellInput(new TextBox { Text = entry.ExePath, ReadOnly = true, TabStop = true, AccessibleName = "실행 경로" }), 1, 1);

        p.Controls.Add(UiStyle.CellLabel("윈도우 클래스"), 0, 2);
        p.Controls.Add(UiStyle.CellInput(new TextBox { Text = entry.ClassName, ReadOnly = true, TabStop = true, AccessibleName = "윈도우 클래스" }), 1, 2);

        p.Controls.Add(UiStyle.CellLabel("타이틀 포함 (선택)"), 0, 3);
        _titleContains = UiStyle.CellInput(new TextBox { Text = entry.TitleContains ?? "", AccessibleName = "타이틀 포함 (선택)" });
        p.Controls.Add(_titleContains, 1, 3);

        p.Controls.Add(UiStyle.CellLabel("배치 위치/크기"), 0, 4);
        p.Controls.Add(UiStyle.CellLabel("픽셀 단위. X, Y: 왼쪽 위 모서리의 화면 좌표 · 너비, 높이: 창 크기", SystemColors.GrayText), 1, 4);

        // Dedicated 4-column grid for X/Y/W/H (captions row + inputs row) so no field can wrap away.
        var rectCaptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        var rectInputs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        for (int i = 0; i < 4; i++)
        {
            rectCaptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            rectInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        }
        rectCaptions.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rectInputs.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rectCaptions.Controls.Add(UiStyle.CellLabel("X", SystemColors.GrayText), 0, 0);
        rectCaptions.Controls.Add(UiStyle.CellLabel("Y", SystemColors.GrayText), 1, 0);
        rectCaptions.Controls.Add(UiStyle.CellLabel("너비", SystemColors.GrayText), 2, 0);
        rectCaptions.Controls.Add(UiStyle.CellLabel("높이", SystemColors.GrayText), 3, 0);
        _x = UiStyle.CellInput(NumInt(entry.Rect.X, -32000, 32000, "X"));
        _y = UiStyle.CellInput(NumInt(entry.Rect.Y, -32000, 32000, "Y"));
        _w = UiStyle.CellInput(NumInt(entry.Rect.Width, 50, 32000, "너비"));
        _h = UiStyle.CellInput(NumInt(entry.Rect.Height, 50, 32000, "높이"));
        rectInputs.Controls.Add(_x, 0, 0);
        rectInputs.Controls.Add(_y, 1, 0);
        rectInputs.Controls.Add(_w, 2, 0);
        rectInputs.Controls.Add(_h, 3, 0);
        p.Controls.Add(rectCaptions, 1, 5);
        p.Controls.Add(rectInputs, 1, 6);

        _max = new CheckBox
        {
            Text = "최대화 상태로 복원 (위치/크기는 복원했을 때의 값)",
            Checked = entry.Maximized,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(half, 0, half, 0)
        };
        p.Controls.Add(_max, 1, 7);

        var cancel = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.Cancel }, "취소");
        var ok = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.OK }, "확인");
        ok.Click += OnOk;
        var buttons = UiStyle.Footer(Array.Empty<(Button, int)>(),
            new (Button, int)[] { (ok, UiStyle.PrimaryButtonWidth), (cancel, UiStyle.PrimaryButtonWidth) });
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(p);
        Controls.Add(buttons);
        ResumeLayout(true);
    }

    private static NumericUpDown NumInt(int v, int min, int max, string name) =>
        new() { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, v)), AccessibleName = name };

    private void OnOk(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_role.Text))
        {
            MessageBox.Show(this, "역할 이름을 입력하세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        RoleResult = _role.Text.Trim();
        TitleContainsResult = string.IsNullOrWhiteSpace(_titleContains.Text) ? null : _titleContains.Text.Trim();
        RectResult = new SavedRect { X = (int)_x.Value, Y = (int)_y.Value, Width = (int)_w.Value, Height = (int)_h.Value };
        MaximizedResult = _max.Checked;
    }
}
