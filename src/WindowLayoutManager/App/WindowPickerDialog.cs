using WindowLayoutManager.Model;
using WindowLayoutManager.Services;
using WindowLayoutManager.Native;

namespace WindowLayoutManager.App;

public sealed class WindowPickerDialog : Form
{
    private readonly ListView _list;
    private readonly TextBox _nameBox;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly IWindowProvider _provider;
    private readonly Label _staleWarnLabel;
    private readonly Button _refreshBtn;

    public string ProfileName => _nameBox.Text.Trim();
    public List<PickedWindow> Picked { get; } = new();

    public sealed record PickedWindow(WindowInfo Snapshot, WindowInfo Fresh, string Role, string? TitleContains);

    public WindowPickerDialog(IWindowProvider provider, IReadOnlyList<WindowInfo> initialWindows, string suggestedName, bool nameEditable = true)
    {
        _provider = provider;
        SuspendLayout();
        UiStyle.ApplyForm(this);
        Text = "현재 창에서 배치 저장";
        Icon = AppIcon.ForForm; // shared process-lifetime icon; not owned/disposed by the dialog
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(880, 540);
        Size = new Size(980, 640);
        ShowInTaskbar = false;

        int pad = UiStyle.OuterPadding, half = UiStyle.HalfGap;
        // Top: [배치 이름][name ……………][현재 창 다시 읽기]  /  refresh notice spanning the row.
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiStyle.RowHeight + 28 + (pad - half),
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(pad - half, pad - half, pad - half, 0)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiStyle.SecondaryButtonWidth + UiStyle.Gap));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.RowHeight));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        top.Controls.Add(UiStyle.CellLabel("배치 이름"), 0, 0);
        _nameBox = UiStyle.CellInput(new TextBox { Text = suggestedName, Enabled = nameEditable, AccessibleName = "배치 이름" });
        top.Controls.Add(_nameBox, 1, 0);
        _refreshBtn = UiStyle.ConfigureButton(new Button(), "현재 창 다시 읽기");
        _refreshBtn.Click += (_, _) => RefreshSnapshot();
        top.Controls.Add(_refreshBtn, 2, 0);
        _staleWarnLabel = UiStyle.CellLabel("", Color.DarkOrange);
        top.Controls.Add(_staleWarnLabel, 0, 1);
        top.SetColumnSpan(_staleWarnLabel, 3);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true,
            AccessibleName = "저장할 창 목록"
        };
        _list.Columns.Add("역할 이름", LogicalToDeviceUnits(160));
        _list.Columns.Add("프로그램", LogicalToDeviceUnits(140));
        _list.Columns.Add("제목", LogicalToDeviceUnits(300));
        _list.Columns.Add("타이틀 조각", LogicalToDeviceUnits(150));
        _list.Columns.Add("현재 위치", LogicalToDeviceUnits(180));
        // Long titles stay readable: hover shows the full cell text.
        _list.ShowItemToolTips = true;
        var listWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(pad, half, pad, 0) };
        listWrap.Controls.Add(_list);

        PopulateList(initialWindows);

        _ok = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.OK }, "저장");
        _cancel = UiStyle.ConfigureButton(new Button { DialogResult = DialogResult.Cancel }, "취소");
        var bottom = UiStyle.Footer(Array.Empty<(Button, int)>(),
            new (Button, int)[] { (_ok, UiStyle.PrimaryButtonWidth), (_cancel, UiStyle.PrimaryButtonWidth) });
        AcceptButton = _ok;
        CancelButton = _cancel;
        _ok.Click += OnOk;

        Controls.Add(listWrap);
        Controls.Add(bottom);
        Controls.Add(top);
        ResumeLayout(true);
    }

    /// <summary>QA: name box, refresh, list, save and cancel.</summary>
    public IReadOnlyList<Control> UiSmokeKeyControls => new Control[] { _nameBox, _refreshBtn, _list, _ok, _cancel };

    private void PopulateList(IReadOnlyList<WindowInfo> windows)
    {
        _list.Items.Clear();
        var groups = windows.GroupBy(w => (w.ExeKey, w.ClassName)).ToDictionary(g => g.Key, g => g.ToList());
        int roleIdx = 0;
        foreach (var w in windows)
        {
            roleIdx++;
            var group = groups[(w.ExeKey, w.ClassName)];
            string? suggested = null;
            if (group.Count > 1)
            {
                var siblings = group.Where(x => x.Identity != w.Identity).Select(x => x.Title);
                suggested = Services.LayoutEngine.SuggestTitleContainsFor(w.Title, siblings);
            }
            var role = group.Count == 1 ? w.ProcessName : $"{w.ProcessName} {roleIdx}";
            var rect = w.ShowState == WindowShowState.Normal ? w.CurrentRect : w.NormalRect;
            var rectText = $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}";
            if (w.ShowState == WindowShowState.Maximized) rectText += " [최대화]";
            else if (w.ShowState == WindowShowState.Minimized) rectText += " [최소화]";
            var li = new ListViewItem(new[]
            {
                role, w.ProcessName, w.Title, suggested ?? "", rectText
            }) { Tag = w, Checked = true };
            _list.Items.Add(li);
        }
    }

    private void RefreshSnapshot()
    {
        var fresh = _provider.Enumerate();
        PopulateList(fresh.Windows);
        _staleWarnLabel.Text = $"새로 읽음: {fresh.Windows.Count}개 창. 체크/역할/타이틀 조건을 다시 확인하세요.";
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            MessageBox.Show(this, "배치 이름을 입력하세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        // Re-enumerate at save-time to refresh identities/geometry; warn if checked windows disappeared.
        var freshSnap = _provider.Enumerate();
        var freshById = freshSnap.Windows.ToDictionary(w => w.Identity);
        Picked.Clear();
        var missing = new List<string>();
        foreach (ListViewItem li in _list.Items)
        {
            if (!li.Checked) continue;
            var w = (WindowInfo)li.Tag!;
            if (!freshById.TryGetValue(w.Identity, out var fresh))
            {
                missing.Add($"{w.ProcessName}: {w.Title}");
                continue;
            }
            var role = li.SubItems[0].Text.Trim();
            if (string.IsNullOrEmpty(role)) role = fresh.ProcessName;
            var tc = li.SubItems[3].Text.Trim();
            Picked.Add(new PickedWindow(w, fresh, role, string.IsNullOrEmpty(tc) ? null : tc));
        }
        if (Picked.Count == 0)
        {
            MessageBox.Show(this, "저장할 창이 없습니다. 사라졌거나 선택되지 않았습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        if (missing.Count > 0)
        {
            MessageBox.Show(this, $"{missing.Count}개 창이 사라져 저장에서 제외됩니다:\n\n{string.Join("\n", missing)}", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
