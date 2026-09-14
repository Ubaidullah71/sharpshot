namespace Sharpshot.UI.Controls;

internal sealed class ToggleSwitch : ThemedControl
{
    private bool _checked;
    private bool _hover;

    public ToggleSwitch()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (value == _checked)
            {
                return;
            }

            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle TrackBounds => new(Width - Dp(40) - Dp(2), (Height - Dp(20)) / 2, Dp(40), Dp(20));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var label = Math.Max(MeasureText("On", Font).Width, MeasureText("Off", Font).Width);
        return new Size(label + Dp(12) + Dp(44), Dp(34));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var track = TrackBounds;
        var trackF = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f);
        var radius = trackF.Height / 2f;
        var knobSize = Dpf(_hover ? 14 : 12);
        var knobY = track.Y + (track.Height - knobSize) / 2f;

        if (_checked)
        {
            Theme.FillRounded(g, !Enabled ? P.DisabledText : _hover ? P.AccentHover : P.Accent, trackF, radius);
            using var knob = new SolidBrush(P.OnAccent);
            g.FillEllipse(knob, track.Right - Dpf(4) - knobSize, knobY, knobSize, knobSize);
        }
        else
        {
            Theme.FillRounded(g, _hover ? P.ControlHover : P.Control, trackF, radius);
            Theme.DrawRounded(g, Enabled ? P.SecondaryText : P.DisabledText, trackF, radius);
            using var knob = new SolidBrush(Enabled ? P.SecondaryText : P.DisabledText);
            g.FillEllipse(knob, track.X + Dpf(4), knobY, knobSize, knobSize);
        }

        DrawText(g, _checked ? "On" : "Off", Font, new Rectangle(0, 0, track.X - Dp(12), Height), Enabled ? P.Text : P.DisabledText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (Focused && ShowFocusCues)
        {
            Theme.DrawRounded(g, P.Text, RectangleF.Inflate(trackF, Dpf(3), Dpf(3)), radius + Dpf(3), Dpf(2));
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            Checked = !Checked;
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;

        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override string? DefaultAction => owner.Checked ? "Turn off" : "Turn on";

        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }
}