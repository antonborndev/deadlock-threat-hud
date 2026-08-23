using System.Drawing;
using System.Windows.Forms;

internal enum BridgeActionButtonTone
{
    Blue,
    Purple
}

internal static class BridgeUiTheme
{
    public static readonly Color Window =
        Color.FromArgb(
            24,
            24,
            27
        );

    public static readonly Color Surface =
        Color.FromArgb(
            16,
            16,
            18
        );

    public static readonly Color SurfaceRaised =
        Color.FromArgb(
            34,
            34,
            38
        );

    public static readonly Color SurfaceHover =
        Color.FromArgb(
            48,
            48,
            54
        );

    public static readonly Color SurfacePressed =
        Color.FromArgb(
            58,
            58,
            65
        );

    public static readonly Color Border =
        Color.FromArgb(
            58,
            58,
            64
        );

    public static readonly Color Text =
        Color.WhiteSmoke;

    public static readonly Color TextMuted =
        Color.Silver;

    public static readonly Color Link =
        Color.LightSkyBlue;

    public static readonly Color ServiceInProgress =
        Color.White;

    public static readonly Color ServiceCompleted =
        Color.LightGreen;

    public static readonly Color ServiceError =
        Color.LightCoral;

    public static readonly Color ActionSurface =
        Color.FromArgb(
            43,
            46,
            54
        );

    public static readonly Color ActionSurfaceHover =
        Color.FromArgb(
            52,
            57,
            68
        );

    public static readonly Color ActionSurfacePressed =
        Color.FromArgb(
            36,
            40,
            48
        );

    public static readonly Color ActionSurfaceDisabled =
        Color.FromArgb(
            29,
            29,
            33
        );

    public static readonly Color ActionBorderDisabled =
        Color.FromArgb(
            68,
            68,
            76
        );

    public static readonly Color ActionTextDisabled =
        Color.FromArgb(
            142,
            142,
            152
        );

    public static readonly Color ActionBlue =
        Color.FromArgb(
            91,
            170,
            224
        );

    public static readonly Color ActionPurple =
        Color.FromArgb(
            181,
            127,
            229
        );

    public static Button CreateButton(
        string text,
        int width
    )
    {
        var button =
            new Button
            {
                Text =
                    text,

                Width =
                    width,

                Height =
                    30,

                BackColor =
                    SurfaceRaised,

                ForeColor =
                    Text,

                FlatStyle =
                    FlatStyle.Flat,

                UseVisualStyleBackColor =
                    false,

                Margin =
                    new Padding(
                        0
                    ),

                Padding =
                    new Padding(
                        0
                    ),

                TextAlign =
                    ContentAlignment.MiddleCenter
            };

        button.FlatAppearance.BorderSize =
            0;

        button.FlatAppearance.MouseOverBackColor =
            SurfaceHover;

        button.FlatAppearance.MouseDownBackColor =
            SurfacePressed;

        return button;
    }

    public static Button CreateActionButton(
        string text,
        int width,
        BridgeActionButtonTone tone
    )
    {
        return new BridgeActionButton(
            tone
        )
        {
            Text =
                text,

            Width =
                width,

            Height =
                34,

            Margin =
                new Padding(
                    0
                ),

            Padding =
                new Padding(
                    8,
                    0,
                    8,
                    0
                ),

            Font =
                new Font(
                    "Segoe UI",
                    9F,
                    FontStyle.Bold,
                    GraphicsUnit.Point
                )
        };
    }

    public static void SetNavigationSelected(
        Button button,
        bool selected
    )
    {
        button.BackColor =
            selected
                ? SurfaceHover
                : SurfaceRaised;

        button.ForeColor =
            selected
                ? Color.White
                : TextMuted;
    }
}

internal sealed class BridgeActionButton : Button
{
    private readonly Color _accentColor;

    private bool _hovered;
    private bool _pressed;

    public BridgeActionButton(
        BridgeActionButtonTone tone
    )
    {
        _accentColor =
            tone ==
                BridgeActionButtonTone.Purple
                ? BridgeUiTheme.ActionPurple
                : BridgeUiTheme.ActionBlue;

        FlatStyle =
            FlatStyle.Flat;

        FlatAppearance.BorderSize =
            0;

        UseVisualStyleBackColor =
            false;

        TextAlign =
            ContentAlignment.MiddleCenter;

        TabStop =
            true;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true
        );
    }

    protected override void OnPaint(
        PaintEventArgs e
    )
    {
        var backgroundColor =
            !Enabled
                ? BridgeUiTheme.ActionSurfaceDisabled
                : _pressed
                    ? BridgeUiTheme.ActionSurfacePressed
                    : _hovered
                        ? BridgeUiTheme.ActionSurfaceHover
                        : BridgeUiTheme.ActionSurface;

        var borderColor =
            Enabled
                ? _accentColor
                : BridgeUiTheme.ActionBorderDisabled;

        var textColor =
            Enabled
                ? Color.White
                : BridgeUiTheme.ActionTextDisabled;

        using (
            var backgroundBrush =
                new SolidBrush(
                    backgroundColor
                )
        )
        {
            e.Graphics.FillRectangle(
                backgroundBrush,
                ClientRectangle
            );
        }

        var borderBounds =
            new Rectangle(
                0,
                0,
                Math.Max(
                    0,
                    ClientSize.Width - 1
                ),
                Math.Max(
                    0,
                    ClientSize.Height - 1
                )
            );

        using (
            var borderPen =
                new Pen(
                    borderColor,
                    1F
                )
        )
        {
            e.Graphics.DrawRectangle(
                borderPen,
                borderBounds
            );
        }

        using (
            var accentBrush =
                new SolidBrush(
                    borderColor
                )
        )
        {
            e.Graphics.FillRectangle(
                accentBrush,
                new Rectangle(
                    1,
                    1,
                    3,
                    Math.Max(
                        0,
                        ClientSize.Height - 2
                    )
                )
            );
        }

        var textBounds =
            new Rectangle(
                8,
                0,
                Math.Max(
                    0,
                    ClientSize.Width - 16
                ),
                ClientSize.Height
            );

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine
        );

        if (
            Enabled &&
            Focused &&
            ShowFocusCues
        )
        {
            var focusBounds =
                Rectangle.Inflate(
                    borderBounds,
                    -4,
                    -4
                );

            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                focusBounds,
                textColor,
                backgroundColor
            );
        }
    }

    protected override void OnMouseEnter(
        EventArgs e
    )
    {
        base.OnMouseEnter(
            e
        );

        _hovered =
            true;

        Invalidate();
    }

    protected override void OnMouseLeave(
        EventArgs e
    )
    {
        base.OnMouseLeave(
            e
        );

        _hovered =
            false;

        _pressed =
            false;

        Invalidate();
    }

    protected override void OnMouseDown(
        MouseEventArgs e
    )
    {
        base.OnMouseDown(
            e
        );

        if (
            Enabled &&
            e.Button ==
                MouseButtons.Left
        )
        {
            _pressed =
                true;

            Invalidate();
        }
    }

    protected override void OnMouseUp(
        MouseEventArgs e
    )
    {
        base.OnMouseUp(
            e
        );

        _pressed =
            false;

        Invalidate();
    }

    protected override void OnKeyDown(
        KeyEventArgs e
    )
    {
        base.OnKeyDown(
            e
        );

        if (
            Enabled &&
            e.KeyCode is
                Keys.Space or
                Keys.Enter
        )
        {
            _pressed =
                true;

            Invalidate();
        }
    }

    protected override void OnKeyUp(
        KeyEventArgs e
    )
    {
        base.OnKeyUp(
            e
        );

        _pressed =
            false;

        Invalidate();
    }

    protected override void OnEnabledChanged(
        EventArgs e
    )
    {
        base.OnEnabledChanged(
            e
        );

        if (!Enabled)
        {
            _hovered =
                false;

            _pressed =
                false;
        }

        Invalidate();
    }

    protected override void OnGotFocus(
        EventArgs e
    )
    {
        base.OnGotFocus(
            e
        );

        Invalidate();
    }

    protected override void OnLostFocus(
        EventArgs e
    )
    {
        base.OnLostFocus(
            e
        );

        _pressed =
            false;

        Invalidate();
    }
}
