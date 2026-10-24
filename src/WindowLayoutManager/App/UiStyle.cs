namespace WindowLayoutManager.App;

/// <summary>
/// Shared v0.3.2 typography and spacing for every WindowDock form.
/// All sizes are LOGICAL pixels at 96 DPI. Forms opt in to AutoScaleMode.Dpi with
/// AutoScaleDimensions 96×96 (inside Suspend/ResumeLayout), so WinForms scales control bounds and
/// TableLayoutPanel absolute rows/columns together with the font on high-DPI displays. Values that
/// auto-scaling does not touch (ListView column widths, splitter distances) are converted with
/// Control.LogicalToDeviceUnits at the point of use.
/// </summary>
public static class UiStyle
{
    public const float BodyPoints = 9.5f;
    public const int ButtonHeight = 34;
    public const int Gap = 8;
    public const int HalfGap = Gap / 2;
    public const int OuterPadding = 12;
    public const int RowHeight = 36;
    public const int PrimaryButtonWidth = 104;   // 확인 / 저장 / 취소
    public const int SecondaryButtonWidth = 150; // 기본 키 조합으로 / 현재 창 다시 읽기

    private static readonly Lazy<Font> Body = new(CreateBodyFont);
    private static readonly Lazy<Font> BodyBold = new(() => new Font(Body.Value, FontStyle.Bold));

    /// <summary>Korean-capable body font (Malgun Gothic), falling back to the system UI font.
    /// Shared for the process lifetime; never disposed.</summary>
    public static Font BodyFont => Body.Value;

    /// <summary>Same family/size, bold — only for the active-profile label and section captions.</summary>
    public static Font BoldFont => BodyBold.Value;

    private static Font CreateBodyFont()
    {
        try
        {
            var f = new Font("Malgun Gothic", BodyPoints, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(f.Name, "Malgun Gothic", StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        catch { }
        var family = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;
        return new Font(family, BodyPoints, FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>Call first in a form constructor, after SuspendLayout().</summary>
    public static void ApplyForm(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = BodyFont;
    }

    /// <summary>Standard action button: fixed height, fills its layout cell, end-ellipsis.</summary>
    public static Button ConfigureButton(Button b, string text)
    {
        b.Text = text;
        b.AccessibleName ??= text;
        b.UseMnemonic = false;
        b.AutoEllipsis = true;
        b.Dock = DockStyle.Fill;
        b.Margin = new Padding(HalfGap);
        b.MinimumSize = new Size(0, ButtonHeight);
        b.Height = ButtonHeight;
        return b;
    }

    /// <summary>Section caption (bold, same size as body).</summary>
    public static Label Caption(string text) => new()
    {
        Text = text,
        Font = BoldFont,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(HalfGap, 0, HalfGap, 0)
    };

    /// <summary>Single-line label that fills its cell and centers vertically (row alignment).</summary>
    public static Label CellLabel(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        UseMnemonic = false,
        ForeColor = color ?? SystemColors.ControlText,
        Margin = new Padding(HalfGap, 0, HalfGap, 0)
    };

    /// <summary>Input placed in a fixed-height row: stretched horizontally, centered vertically.</summary>
    public static T CellInput<T>(T c) where T : Control
    {
        c.Dock = DockStyle.None;
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(HalfGap, 0, HalfGap, 0);
        return c;
    }

    /// <summary>
    /// Dialog footer on one baseline: optional left buttons, flexible gap, right buttons.
    /// Every button has the same height; widths are fixed per role.
    /// </summary>
    public static TableLayoutPanel Footer(IReadOnlyList<(Button button, int width)> left, IReadOnlyList<(Button button, int width)> right)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            RowCount = 1,
            ColumnCount = left.Count + 1 + right.Count,
            Height = ButtonHeight + Gap + 2 * (OuterPadding - HalfGap),
            Padding = new Padding(OuterPadding - HalfGap)
        };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        int col = 0;
        foreach (var (b, w) in left)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w + Gap));
            t.Controls.Add(b, col++, 0);
        }
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        col++;
        foreach (var (b, w) in right)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w + Gap));
            t.Controls.Add(b, col++, 0);
        }
        return t;
    }
}
