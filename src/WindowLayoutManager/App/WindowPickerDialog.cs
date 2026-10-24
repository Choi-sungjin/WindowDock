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
        Text = "현재 창에서 배치 저장";
        Icon = AppIcon.ForForm; // shared process-lifetime icon; not owned/disposed by the dialog
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(880, 540);
        Size = new Size(980, 640);
        Font = new Font("Segoe UI", 9f);

        var top = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(10, 8, 10, 4) };
        var lbl = new Label { Text = "배치 이름:", AutoSize = true, Location = new Point(10, 14) };
        _nameBox = new TextBox { Location = new Point(90, 10), Width = 400, Text = suggestedName, Enabled = nameEditable };
        _refreshBtn = new Button { Text = "현재 창 다시 읽기", Location = new Point(510, 8), Width = 160, Height = 28 };
        _refreshBtn.Click += (_, _) => RefreshSnapshot();
        _staleWarnLabel = new Label { AutoSize = true, Location = new Point(10, 42), ForeColor = Color.DarkOrange };
        top.Controls.Add(lbl);
        top.Controls.Add(_nameBox);
        top.Controls.Add(_refreshBtn);
        top.Controls.Add(_staleWarnLabel);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true,
        };
        _list.Columns.Add("역할 이름", 160);
        _list.Columns.Add("프로그램", 140);
        _list.Columns.Add("제목", 300);
        _list.Columns.Add("타이틀 조각", 150);
        _list.Columns.Add("현재 위치", 180);

        PopulateList(initialWindows);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        _ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Width = 100, Height = 28, Location = new Point(bottom.Width - 230, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 100, Height = 28, Location = new Point(bottom.Width - 120, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        bottom.Controls.Add(_ok);
        bottom.Controls.Add(_cancel);
        AcceptButton = _ok;
        CancelButton = _cancel;
        _ok.Click += OnOk;

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
    }

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
