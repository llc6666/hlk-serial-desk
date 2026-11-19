using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class DeskStyle
{
    internal static readonly Color Paper = Hex(0xF3F4F8);
    internal static readonly Color Surface = Hex(0xFFFFFF);
    internal static readonly Color Ink = Hex(0x15182C);
    internal static readonly Color Muted = Hex(0x515C73);
    internal static readonly Color Border = Hex(0xDDE2EC);
    internal static readonly Color Accent = Hex(0x6334CE);
    internal static readonly Color AccentDark = Hex(0x5127B2);
    internal static readonly Color Console = Hex(0x111727);
    internal static readonly Color ConsoleBar = Hex(0x192136);
    internal static readonly Color ConsoleText = Hex(0xDEE7F9);

    internal static Color Hex(int rgb)
    {
        return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    }

    internal static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (diameter < 2f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DeskButton : Button
{
    private readonly bool primary;
    private bool mouseOver, mouseDown, spaceDown;

    internal DeskButton(string text, bool primary = false)
    {
        this.primary = primary;
        Text = text;
        UseVisualStyleBackColor = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 3 || Height < 3) return;
        using (var canvas = new SolidBrush(Parent == null ? DeskStyle.Surface : Parent.BackColor))
            e.Graphics.FillRectangle(canvas, ClientRectangle);

        bool pressed = (mouseDown && mouseOver) || spaceDown;
        Color fill, edge, fore;
        if (!Enabled)
        {
            fill = DeskStyle.Paper;
            edge = DeskStyle.Border;
            fore = DeskStyle.Hex(0x69748A);
        }
        else if (primary)
        {
            fill = pressed ? DeskStyle.Hex(0x43209A) : mouseOver ? DeskStyle.AccentDark : DeskStyle.Accent;
            edge = fill;
            fore = DeskStyle.Surface;
        }
        else
        {
            fill = pressed ? DeskStyle.Hex(0xEAE3FC) : mouseOver ? DeskStyle.Hex(0xF7F3FF) : DeskStyle.Surface;
            edge = (mouseOver || Focused) ? DeskStyle.Accent : DeskStyle.Border;
            fore = (mouseOver || pressed) ? DeskStyle.AccentDark : DeskStyle.Ink;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Math.Min(9f, Height / 4f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DeskStyle.Round(rect, radius))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(edge))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        if (Enabled && Focused && Width > 8 && Height > 8)
        {
            RectangleF outline = rect;
            outline.Inflate(-2.5f, -2.5f);
            using (var path = DeskStyle.Round(outline, radius - 2f))
            using (var pen = new Pen(primary ? DeskStyle.Surface : DeskStyle.Accent, 1f))
            {
                pen.DashStyle = DashStyle.Dot;
                e.Graphics.DrawPath(pen, path);
            }
        }

        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;

        var content = new Rectangle(5, pressed ? 1 : 0, Math.Max(1, Width - 10), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, content, fore, flags);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); mouseOver = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouseOver = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) mouseDown = true;
        base.OnMouseDown(e);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) mouseDown = false;
        base.OnMouseUp(e);
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) mouseDown = false;
        base.OnMouseCaptureChanged(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) spaceDown = true;
        base.OnKeyDown(e);
        Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) spaceDown = false;
        base.OnKeyUp(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { spaceDown = false; base.OnLostFocus(e); Invalidate(); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) mouseDown = spaceDown = false;
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

internal sealed class DeskPill : Control
{
    private Color fill = DeskStyle.Hex(0xE8EBF2);
    private Color ink = DeskStyle.Muted;

    internal DeskPill(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        BackColor = Color.Transparent;
        Text = text;
        AccessibleName = text;
    }

    public void Set(string text, Color back, Color fore)
    {
        bool redraw = fill != back || ink != fore || Text != text;
        fill = back;
        ink = fore;
        Text = text;
        if (redraw) Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 3 || Height < 3) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DeskStyle.Round(new RectangleF(0f, 0f, Width, Height), Height / 2f))
        using (var brush = new SolidBrush(fill))
        using (var dot = new SolidBrush(ink))
        {
            e.Graphics.FillPath(brush, path);
            float diameter = Math.Max(4f, Math.Min(7f, Height * .23f));
            float left = Math.Min(11f, Height * .41f);
            e.Graphics.FillEllipse(dot, left, (Height - diameter) / 2f, diameter, diameter);

            int textLeft = (int)Math.Ceiling(left + diameter + 5f);
            if (Width > textLeft + 5)
            {
                var content = new Rectangle(textLeft, 0, Width - textLeft - 5, Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, content, ink,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}

internal sealed class DeskMark : Control
{
    internal DeskMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "串口终端标志";
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float side = Math.Min(Width, Height) - 1f;
        if (side < 2f) return;
        var rect = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DeskStyle.Round(rect, side * .26f))
        using (var brush = new LinearGradientBrush(rect, DeskStyle.Accent, DeskStyle.AccentDark, 50f))
        using (var pen = new Pen(DeskStyle.Surface, Math.Max(1.4f, side * .064f)))
        {
            e.Graphics.FillPath(brush, path);
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = pen.EndCap = LineCap.Round;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(rect.X + side * .27f, rect.Y + side * .32f),
                new PointF(rect.X + side * .45f, rect.Y + side * .49f),
                new PointF(rect.X + side * .27f, rect.Y + side * .66f)
            });
            e.Graphics.DrawLine(pen, rect.X + side * .53f, rect.Y + side * .66f,
                rect.X + side * .74f, rect.Y + side * .66f);
        }
    }
}
