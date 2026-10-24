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
        Text = $"항목 편집 — {entry.Role}";
        Icon = AppIcon.ForForm; // shared process-lifetime icon; not owned/disposed by the dialog
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 460);
        Size = new Size(680, 480);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;

        var p = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(14, 14, 14, 14),
            AutoSize = false,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 5; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        p.Controls.Add(MakeLabel("역할 이름:"), 0, 0);
        _role = new TextBox { Text = entry.Role, Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.Fill };
        p.Controls.Add(_role, 1, 0);

        p.Controls.Add(MakeLabel("실행 경로:"), 0, 1);
        p.Controls.Add(new Label { Text = entry.ExePath, AutoEllipsis = true, AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.Fill }, 1, 1);

        p.Controls.Add(MakeLabel("윈도우 클래스:"), 0, 2);
        p.Controls.Add(new Label { Text = entry.ClassName, AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.Fill }, 1, 2);

        p.Controls.Add(MakeLabel("타이틀 포함(선택):"), 0, 3);
        _titleContains = new TextBox { Text = entry.TitleContains ?? "", Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.Fill };
        p.Controls.Add(_titleContains, 1, 3);

        p.Controls.Add(MakeLabel("배치 위치/크기:"), 0, 4);
        var rectHint = new Label
        {
            Text = "픽셀 단위. X,Y: 왼쪽 위 모서리의 화면 좌표. 너비·높이: 창 크기.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        p.Controls.Add(rectHint, 1, 4);

        // Dedicated 2x4 grid for X/Y/W/H so no wrapping can hide any field.
        var rectGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, AutoSize = false };
        for (int i = 0; i < 4; i++) rectGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        rectGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        rectGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rectGrid.Controls.Add(SmallLabel("X"), 0, 0);
        rectGrid.Controls.Add(SmallLabel("Y"), 1, 0);
        rectGrid.Controls.Add(SmallLabel("너비"), 2, 0);
        rectGrid.Controls.Add(SmallLabel("높이"), 3, 0);
        _x = NumInt(entry.Rect.X, -32000, 32000);
        _y = NumInt(entry.Rect.Y, -32000, 32000);
        _w = NumInt(entry.Rect.Width, 50, 32000);
        _h = NumInt(entry.Rect.Height, 50, 32000);
        _x.Dock = _y.Dock = _w.Dock = _h.Dock = DockStyle.Fill;
        rectGrid.Controls.Add(_x, 0, 1);
        rectGrid.Controls.Add(_y, 1, 1);
        rectGrid.Controls.Add(_w, 2, 1);
        rectGrid.Controls.Add(_h, 3, 1);
        p.Controls.Add(MakeLabel(""), 0, 5);
        p.Controls.Add(rectGrid, 1, 5);

        _max = new CheckBox { Text = "최대화 상태로 복원 (지정 위치/크기는 복원 시의 저장값)", Checked = entry.Maximized, AutoSize = true };
        p.Controls.Add(MakeLabel(""), 0, 6);
        p.Controls.Add(_max, 1, 6);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 100, Height = 30 };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Width = 100, Height = 30 };
        ok.Click += OnOk;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(p);
        Controls.Add(buttons);
    }

    private static Label MakeLabel(string t) => new()
    {
        Text = t,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 10, 6, 0)
    };

    private static Label SmallLabel(string t) => new()
    {
        Text = t,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(2, 0, 0, 0),
        ForeColor = Color.FromArgb(80, 80, 80)
    };

    private static NumericUpDown NumInt(int v, int min, int max) =>
        new() { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, v)) };

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
