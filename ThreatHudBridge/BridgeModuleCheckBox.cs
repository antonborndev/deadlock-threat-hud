using System.Drawing;
using System.Windows.Forms;

internal sealed class BridgeModuleCheckBox : CheckBox
{
    private const int GlyphSize = 13;
    private const int TextGap = 7;

    public BridgeModuleCheckBox()
    {
        Appearance = Appearance.Normal;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true
        );
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);

        var scale = Math.Max(1F, DeviceDpi / 96F);
        var size = Math.Min(
            Math.Max(8, ClientSize.Height - 4),
            Math.Max(8, (int)Math.Round(GlyphSize * scale))
        );

        var box = new Rectangle(
            1,
            Math.Max(0, (ClientSize.Height - size) / 2),
            size,
            size
        );

        var glyphColor = Checked && Enabled
            ? BridgeUiTheme.ServiceCompleted
            : BridgeUiTheme.ActionTextDisabled;

        using (var background = new SolidBrush(BridgeUiTheme.Surface))
        {
            e.Graphics.FillRectangle(background, box);
        }

        using (var border = new Pen(glyphColor, Math.Max(1F, scale)))
        {
            e.Graphics.DrawRectangle(
                border,
                box.X,
                box.Y,
                Math.Max(0, box.Width - 1),
                Math.Max(0, box.Height - 1)
            );
        }

        if (Checked)
        {
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var check = new Pen(glyphColor, Math.Max(2F, 2F * scale));
            e.Graphics.DrawLines(
                check,
                new[]
                {
                    new Point(box.Left + size * 2 / 10, box.Top + size * 5 / 10),
                    new Point(box.Left + size * 4 / 10, box.Top + size * 8 / 10),
                    new Point(box.Left + size * 8 / 10, box.Top + size * 2 / 10)
                }
            );
        }

        var textLeft = box.Right + Math.Max(4, (int)Math.Round(TextGap * scale));
        var textBounds = new Rectangle(
            textLeft,
            0,
            Math.Max(0, ClientSize.Width - textLeft),
            ClientSize.Height
        );

        var textColor = Enabled
            ? ForeColor
            : BridgeUiTheme.ActionTextDisabled;

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis
        );

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                textBounds,
                textColor,
                BackColor
            );
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}
