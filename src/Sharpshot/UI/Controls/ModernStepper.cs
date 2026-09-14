namespace Sharpshot.UI.Controls;

internal sealed class ModernStepper : ThemedControl
{
    private enum Part
    {
        None,
        Minus,
        Plus,
    }

    private int _value;
    private int _minimum;
    private int _maximum = 100;
    private Part _hover;
    private Part _pressed;
    private int _wheelDelta;

    public ModernStepper()
    {
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event EventHandler? ValueChanged;

    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            _maximum = Math.Max(_maximum, value);
            Value = _value;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            _minimum = Math.Min(_minimum, value);
            Value = _value;
            Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _minimum, _maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new StepperAccessibleObject(this);

    private Rectangle MinusBounds => new(0, 0, Dp(36), Height);

    private Rectangle PlusBounds => new(Width - Dp(36), 0, Dp(36), Height);

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(132), Dp(34));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var radius = Dpf(4);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRounded(g, P.Control, bounds, radius);

        using (var clip = Theme.RoundedRectangle(bounds, radius))
        {
            g.SetClip(clip);
            foreach (var part in new[] { Part.Minus, Part.Plus })
            {
                if (_hover == part && CanStep(part))
                {
                    using var brush = new SolidBrush(_pressed == part ? P.ControlPressed : P.ControlHover);
                    g.FillRectangle(brush, part == Part.Minus ? MinusBounds : PlusBounds);
                }
            }

            g.ResetClip();
        }

        Theme.DrawRounded(g, P.ControlBorder, bounds, radius);
        using (var divider = new Pen(P.ControlBorder))
        {
            g.DrawLine(divider, MinusBounds.Right, Dp(6), MinusBounds.Right, Height - Dp(6));
            g.DrawLine(divider, PlusBounds.Left, Dp(6), PlusBounds.Left, Height - Dp(6));
        }

        DrawText(g, Glyphs.Remove, IconFont(0.8f), MinusBounds, CanStep(Part.Minus) ? P.Text : P.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawText(g, Glyphs.Add, IconFont(0.8f), PlusBounds, CanStep(Part.Plus) ? P.Text : P.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawText(g, _value.ToString(System.Globalization.CultureInfo.CurrentCulture), DerivedFont(UiFonts.TextSemibold, 1f),
            Rectangle.FromLTRB(MinusBounds.Right, 0, PlusBounds.Left, Height), P.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawFocusRing(g, ClientRectangle, radius);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var part = HitTest(e.Location);
        if (part != _hover)
        {
            _hover = part;
            Cursor = part != Part.None && CanStep(part) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = Part.None;
        _pressed = Part.None;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _pressed = HitTest(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var part = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && part == _pressed)
        {
            Step(part);
        }

        _pressed = Part.None;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // Only whole notches count, so brushing a touchpad over the control doesn't change the value.
        _wheelDelta += e.Delta;
        var steps = _wheelDelta / 120;
        _wheelDelta %= 120;
        Value += steps;
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var handled = true;
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Right: Value++; break;
            case Keys.Down or Keys.Left: Value--; break;
            case Keys.PageUp: Value += 5; break;
            case Keys.PageDown: Value -= 5; break;
            case Keys.Home: Value = _minimum; break;
            case Keys.End: Value = _maximum; break;
            default: handled = false; break;
        }

        e.Handled = handled;
    }

    private Part HitTest(Point point) =>
        MinusBounds.Contains(point) ? Part.Minus : PlusBounds.Contains(point) ? Part.Plus : Part.None;

    private bool CanStep(Part part) => part switch
    {
        Part.Minus => _value > _minimum,
        Part.Plus => _value < _maximum,
        _ => false,
    };

    private void Step(Part part)
    {
        if (part == Part.Minus)
        {
            Value--;
        }
        else if (part == Part.Plus)
        {
            Value++;
        }
    }

    /// <summary>Screen readers get the label as the name and the number as the value.</summary>
    private sealed class StepperAccessibleObject(ModernStepper owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.SpinButton;

        public override string? Value
        {
            get => owner.Value.ToString(System.Globalization.CultureInfo.CurrentCulture);
            set
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var number))
                {
                    owner.Value = number;
                }
            }
        }
    }
}