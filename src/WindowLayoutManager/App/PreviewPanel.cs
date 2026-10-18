using WindowLayoutManager.Model;

namespace WindowLayoutManager.App;

public sealed class PreviewPanel : Panel
{
    private List<ProfileEntry> _entries = new();
    private List<MonitorInfo> _monitors = new();

    public PreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 36);
    }

    public void Set(IEnumerable<ProfileEntry> entries, IEnumerable<MonitorInfo> monitors)
    {
        _entries = entries.ToList();
        _monitors = monitors.ToList();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 10 || Height <= 10) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Compute bounding box of all monitors + all entries
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        bool any = false;
        foreach (var m in _monitors)
        {
            any = true;
            left = Math.Min(left, m.Bounds.X);
            top = Math.Min(top, m.Bounds.Y);
            right = Math.Max(right, m.Bounds.Right);
            bottom = Math.Max(bottom, m.Bounds.Bottom);
        }
        foreach (var e2 in _entries)
        {
            any = true;
            var r = e2.Rect;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }
        if (!any)
        {
            using var brush = new SolidBrush(Color.Gray);
            g.DrawString("저장된 항목이 없습니다.", Font, brush, 10, 10);
            return;
        }

        int virtW = Math.Max(1, right - left);
        int virtH = Math.Max(1, bottom - top);
        float sx = (Width - 20f) / virtW;
        float sy = (Height - 20f) / virtH;
        float s = Math.Min(sx, sy);
        float ox = 10f + ((Width - 20f) - virtW * s) / 2f;
        float oy = 10f + ((Height - 20f) - virtH * s) / 2f;

        // Monitors first
        using var monPen = new Pen(Color.FromArgb(90, 90, 120), 1f);
        using var monBrush = new SolidBrush(Color.FromArgb(48, 48, 60));
        foreach (var m in _monitors)
        {
            var rx = ox + (m.Bounds.X - left) * s;
            var ry = oy + (m.Bounds.Y - top) * s;
            var rw = m.Bounds.Width * s;
            var rh = m.Bounds.Height * s;
            g.FillRectangle(monBrush, rx, ry, rw, rh);
            g.DrawRectangle(monPen, rx, ry, rw, rh);
        }

        // Entries
        using var entryPen = new Pen(Color.FromArgb(140, 220, 220), 2f);
        using var entryBrushNormal = new SolidBrush(Color.FromArgb(96, 60, 150, 220));
        using var entryBrushMax = new SolidBrush(Color.FromArgb(96, 220, 150, 60));
        using var labelBrush = new SolidBrush(Color.White);
        int idx = 0;
        foreach (var e2 in _entries)
        {
            idx++;
            var r = e2.Rect;
            var rx = ox + (r.X - left) * s;
            var ry = oy + (r.Y - top) * s;
            var rw = r.Width * s;
            var rh = r.Height * s;
            g.FillRectangle(e2.Maximized ? entryBrushMax : entryBrushNormal, rx, ry, rw, rh);
            g.DrawRectangle(entryPen, rx, ry, rw, rh);
            var label = $"{idx}. {e2.Role}";
            g.DrawString(label, Font, labelBrush, rx + 4, ry + 4);
        }
    }
}
