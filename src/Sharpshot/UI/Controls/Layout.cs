namespace Sharpshot.UI.Controls;

internal sealed class StackPanel : ThemedControl
{
    private readonly Dictionary<Control, int> _spaceBefore = [];

    /// <summary>Padding in logical (96 DPI) pixels.</summary>
    public Padding Inset { get; set; } = new(32, 24, 32, 16);

    public T Add<T>(T control, int spaceBefore = 0)
        where T : Control
    {
        _spaceBefore[control] = spaceBefore;
        Controls.Add(control);
        return control;
    }

    /// <summary>Takes whatever height is left over, but at least its preferred height.</summary>
    public Control? Fill { get; set; }

    /// <summary>Used to check pages fit without scrolling.</summary>
    public int ContentHeight => Arrange(apply: false);

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, ContentHeight);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Arrange(apply: true);
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);

    private int Arrange(bool apply)
    {
        var x = Dp(Inset.Left);
        var width = Math.Max(0, Width - Dp(Inset.Left) - Dp(Inset.Right));
        var heights = new Dictionary<Control, int>();
        var used = Dp(Inset.Top) + Dp(Inset.Bottom);
        var index = 0;
        foreach (Control child in Controls)
        {
            heights[child] = child.GetPreferredSize(new Size(width, 0)).Height;
            used += heights[child] + (index++ > 0 ? Dp(_spaceBefore.GetValueOrDefault(child)) : 0);
        }

        if (Fill is not null && heights.ContainsKey(Fill))
        {
            heights[Fill] = Math.Max(heights[Fill], heights[Fill] + Height - used);
        }

        var y = Dp(Inset.Top);
        var first = true;
        foreach (Control child in Controls)
        {
            if (!first)
            {
                y += Dp(_spaceBefore.GetValueOrDefault(child));
            }

            var height = apply ? heights[child] : child.GetPreferredSize(new Size(width, 0)).Height;
            if (apply)
            {
                child.SetBounds(x, y, width, height);
            }

            y += height;
            first = false;
        }

        return y + Dp(Inset.Bottom);
    }
}

internal sealed class Card : ThemedControl
{
    public Card()
    {
        BackColor = P.Card;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(0, proposedSize.Width - Dp(2));
        var height = Dp(4) * 2;
        var rows = Rows().ToList();
        foreach (var row in rows)
        {
            height += row.GetPreferredSize(new Size(width, 0)).Height;
        }

        return new Size(proposedSize.Width, height + Math.Max(0, rows.Count - 1) * Dp(1));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Rows are inset slightly so their square corners don't cover the card's rounded ones.
        var y = Dp(4);
        var width = Math.Max(0, Width - Dp(2));
        foreach (var row in Rows())
        {
            var height = row.GetPreferredSize(new Size(width, 0)).Height;
            row.SetBounds(Dp(1), y, width, height);
            y += height + Dp(1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRounded(g, P.Card, bounds, Dpf(6));
        Theme.DrawRounded(g, P.CardBorder, bounds, Dpf(6));

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        using var divider = new SolidBrush(P.Divider);
        var rows = Rows().ToList();
        for (var i = 0; i < rows.Count - 1; i++)
        {
            g.FillRectangle(divider, 1, rows[i].Bottom, Width - 2, Dp(1));
        }
    }

    /// <summary>Control.Visible isn't consulted: it's false while the page is hidden, and rows are never hidden.</summary>
    private IEnumerable<Control> Rows() => Controls.Cast<Control>();
}

internal sealed class SettingRow : ThemedControl
{
    private readonly string? _glyph;
    private string _title;
    private string? _description;

    private readonly Action? _onClick;
    private bool _hover;

    public SettingRow(string? glyph, string title, string? description, Control? editor = null, int editorWidth = 0, Control? below = null)
    {
        _glyph = glyph;
        _title = title;
        _description = description;
        Editor = editor;
        EditorWidth = editorWidth;
        Below = below;
        BackColor = P.Card;
        if (editor is not null)
        {
            // Screen readers announce the editor by the row's title.
            var named = editor is ModernTextBox box ? box.Inner : editor;
            named.AccessibleName ??= title;
            named.AccessibleDescription ??= description;
            Controls.Add(editor);
        }

        if (below is not null)
        {
            Controls.Add(below);
        }
    }

    /// <summary>A whole-row link, like Windows 11's "open in browser" rows.</summary>
    public SettingRow(string glyph, string title, string description, Action onClick, string trailingGlyph = Glyphs.OpenInNew)
        : this(glyph, title, description)
    {
        _onClick = onClick;
        TrailingGlyph = trailingGlyph;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Link;
        AccessibleName = title;
        SetStyle(ControlStyles.Selectable, true);
    }

    private string? TrailingGlyph { get; }

    public Control? Editor { get; }

    public Control? Below { get; }

    /// <summary>Editor width in logical pixels; 0 uses the editor's preferred width.</summary>
    public int EditorWidth { get; }

    public string Title
    {
        get => _title;
        set => ChangeText(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set => ChangeText(ref _description, value);
    }

    /// <summary>Repaints, and re-runs the page layout only if the row's height changed (e.g. text now wraps).</summary>
    private void ChangeText<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        var before = GetPreferredSize(new Size(Width, 0)).Height;
        field = value;
        Invalidate();
        if (GetPreferredSize(new Size(Width, 0)).Height != before)
        {
            Parent?.PerformLayout();
            Parent?.Parent?.PerformLayout();
        }
    }

    private int PadX => Dp(16);

    private int PadY => Dp(12);

    private int TextLeft => _glyph is null ? PadX : PadX + Dp(20) + Dp(16);

    private Font TitleFont => Font;

    private Size EditorSize => Editor is null
        ? Size.Empty
        : new Size(EditorWidth > 0 ? Dp(EditorWidth) : Editor.GetPreferredSize(Size.Empty).Width, Editor.GetPreferredSize(Size.Empty).Height);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var (topHeight, _) = Measure(proposedSize.Width);
        var height = topHeight;
        if (Below is not null)
        {
            height += Below.GetPreferredSize(new Size(proposedSize.Width - TextLeft - PadX, 0)).Height + PadY;
        }

        return new Size(proposedSize.Width, height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var (topHeight, _) = Measure(Width);
        if (Editor is not null)
        {
            var size = EditorSize;
            Editor.SetBounds(Width - PadX - size.Width, (topHeight - size.Height) / 2, size.Width, size.Height);
        }

        if (Below is not null)
        {
            var width = Width - TextLeft - PadX;
            Below.SetBounds(TextLeft, topHeight - Dp(2), width, Below.GetPreferredSize(new Size(width, 0)).Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_onClick is not null && _hover ? Theme.Blend(BackColor, P.Text, 0.04f) : BackColor);
        var (topHeight, textWidth) = Measure(Width);

        if (TrailingGlyph is not null)
        {
            DrawText(g, TrailingGlyph, IconFont(0.95f), new Rectangle(Width - PadX - Dp(20), 0, Dp(20), topHeight), P.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (_onClick is not null && Focused && ShowFocusCues)
        {
            Theme.DrawRounded(g, P.Text, new RectangleF(Dpf(2), Dpf(2), Width - Dpf(4), Height - Dpf(4)), Dpf(4), Dpf(2));
        }

        if (_glyph is not null)
        {
            DrawText(g, _glyph, IconFont(1.3f), new Rectangle(PadX, 0, Dp(20), topHeight), Enabled ? P.Text : P.DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var titleSize = MeasureText(_title, TitleFont, textWidth, TextFormatFlags.WordBreak);
        var descSize = string.IsNullOrEmpty(_description) ? Size.Empty : MeasureText(_description, CaptionFont, textWidth, TextFormatFlags.WordBreak);
        var blockHeight = titleSize.Height + (descSize.IsEmpty ? 0 : Dp(2) + descSize.Height);
        var y = (topHeight - blockHeight) / 2;

        DrawText(g, _title, TitleFont, new Rectangle(TextLeft, y, textWidth, titleSize.Height + Dp(2)), P.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (!descSize.IsEmpty)
        {
            DrawText(g, _description!, CaptionFont, new Rectangle(TextLeft, y + titleSize.Height + Dp(2), textWidth, descSize.Height + Dp(2)),
                P.SecondaryText, TextFormatFlags.WordBreak);
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
            _onClick?.Invoke();
        }
    }

    protected override bool IsInputKey(Keys keyData) => (_onClick is not null && keyData is Keys.Enter or Keys.Space) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_onClick is not null && e.KeyCode is Keys.Enter or Keys.Space)
        {
            _onClick();
            e.Handled = true;
        }
    }

    private (int TopHeight, int TextWidth) Measure(int width)
    {
        var editor = EditorSize;
        var trailing = TrailingGlyph is null ? 0 : Dp(20) + Dp(16);
        var textWidth = Math.Max(Dp(80), width - TextLeft - PadX - trailing - (Editor is null ? 0 : editor.Width + Dp(24)));
        var title = MeasureText(_title, TitleFont, textWidth, TextFormatFlags.WordBreak);
        var desc = string.IsNullOrEmpty(_description) ? Size.Empty : MeasureText(_description, CaptionFont, textWidth, TextFormatFlags.WordBreak);
        var textHeight = title.Height + (desc.IsEmpty ? 0 : Dp(2) + desc.Height);
        var content = Math.Max(textHeight, editor.Height);
        return (Math.Max(Dp(62), content + PadY * 2), textWidth);
    }
}

internal sealed class Field : ThemedControl
{
    private string? _error;

    public Field(string label, ModernTextBox box)
    {
        Text = label;
        Box = box;
        BackColor = P.Card;
        box.Inner.AccessibleName ??= label;
        Controls.Add(box);
        box.TextChanged += (_, _) => Error = null;
    }

    public ModernTextBox Box { get; }

    /// <summary>Small grey text on the label line, shown when there's no error.</summary>
    public string? Hint { get; set; }

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value)
            {
                return;
            }

            _error = value;
            Box.HasError = value is not null;
            Box.Inner.AccessibleDescription = value;
            Invalidate();
        }
    }

    private int LabelHeight => MeasureText("Ag", DerivedFont(UiFonts.TextSemibold, 0.95f)).Height;

    public override Size GetPreferredSize(Size proposedSize) =>
        new(proposedSize.Width, LabelHeight + Dp(6) + Box.GetPreferredSize(Size.Empty).Height);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var top = LabelHeight + Dp(6);
        Box.SetBounds(0, top, Width, Box.GetPreferredSize(Size.Empty).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var labelFont = DerivedFont(UiFonts.TextSemibold, 0.95f);
        var labelWidth = MeasureText(Text, labelFont).Width;
        DrawText(g, Text, labelFont, new Rectangle(0, 0, labelWidth + Dp(2), LabelHeight), Enabled ? P.Text : P.DisabledText, TextFormatFlags.SingleLine);

        if ((_error ?? Hint) is { } note)
        {
            var left = labelWidth + Dp(10);
            DrawText(g, note, CaptionFont, new Rectangle(left, 0, Math.Max(0, Width - left), LabelHeight), _error is null ? P.SecondaryText : P.Error,
                TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}

internal sealed class FieldGrid : ThemedControl
{
    private readonly List<Field[]> _rows = [];

    public FieldGrid()
    {
        BackColor = P.Card;
    }

    public void AddRow(params Field[] fields)
    {
        _rows.Add(fields);
        foreach (var field in fields)
        {
            Controls.Add(field);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var height = Dp(16) * 2 + Math.Max(0, _rows.Count - 1) * Dp(12);
        foreach (var row in _rows)
        {
            height += row.Max(f => f.GetPreferredSize(Size.Empty).Height);
        }

        return new Size(proposedSize.Width, height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = Dp(16);
        var gap = Dp(12);
        var y = pad;
        foreach (var row in _rows)
        {
            var height = row.Max(f => f.GetPreferredSize(Size.Empty).Height);
            var width = (Width - pad * 2 - gap * (row.Length - 1)) / row.Length;
            for (var i = 0; i < row.Length; i++)
            {
                row[i].SetBounds(pad + i * (width + gap), y, width, height);
            }

            y += height + Dp(12);
        }
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);
}

internal enum StatusKind
{
    None,
    Working,
    Success,
    Error,
}

internal sealed class ActionRow : ThemedControl
{
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 16 };
    private StatusKind _kind;
    private string? _message;
    private float _angle;
    private int _buttonWidth;

    public ActionRow(ModernButton button)
    {
        Button = button;
        BackColor = P.Card;
        Controls.Add(button);
        _spinner.Tick += (_, _) =>
        {
            _angle = (_angle + 6f) % 360f;
            Invalidate();
        };
    }

    public ModernButton Button { get; }

    public void SetStatus(StatusKind kind, string? message)
    {
        _kind = kind;
        _message = message;
        _spinner.Enabled = kind == StatusKind.Working;
        Invalidate();
        Parent?.PerformLayout();
        Parent?.Parent?.PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int PadX => Dp(16);

    private int PadY => Dp(12);

    /// <summary>Never shrinks, so the button and message don't jump around as the button's text changes.</summary>
    private int ButtonWidth => _buttonWidth = Math.Max(_buttonWidth, Button.GetPreferredSize(Size.Empty).Width);

    private Rectangle MessageBounds(int width)
    {
        var left = PadX + ButtonWidth + Dp(16) + Dp(26);
        var textWidth = Math.Max(Dp(80), width - left - PadX);
        var size = string.IsNullOrEmpty(_message) ? Size.Empty : MeasureText(_message, Font, textWidth, TextFormatFlags.WordBreak);
        return new Rectangle(left, 0, textWidth, size.Height);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var message = MessageBounds(proposedSize.Width);
        return new Size(proposedSize.Width, PadY * 2 + Math.Max(Button.GetPreferredSize(Size.Empty).Height, message.Height));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var size = Button.GetPreferredSize(Size.Empty);
        Button.SetBounds(PadX, PadY, ButtonWidth, size.Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_kind == StatusKind.None || string.IsNullOrEmpty(_message))
        {
            return;
        }

        var bounds = MessageBounds(Width);
        var buttonHeight = Button.GetPreferredSize(Size.Empty).Height;
        var lineHeight = MeasureText("Ag", Font).Height;
        // One line: centre it on the button. Several lines: start level with the button's text.
        var y = bounds.Height <= lineHeight + Dp(2) ? PadY + (buttonHeight - bounds.Height) / 2 : PadY + (buttonHeight - lineHeight) / 2;

        var iconBox = new Rectangle(bounds.Left - Dp(26), y, Dp(18), lineHeight);
        if (_kind == StatusKind.Working)
        {
            DrawProgressRing(g, new PointF(iconBox.Left + iconBox.Width / 2f, iconBox.Top + iconBox.Height / 2f), 16, _angle);
        }
        else
        {
            var (glyph, color) = _kind == StatusKind.Success ? (Glyphs.Completed, P.Success) : (Glyphs.ErrorBadge, P.Error);
            DrawText(g, glyph, IconFont(1.1f), iconBox, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        DrawText(g, _message, Font, new Rectangle(bounds.Left, y, bounds.Width, bounds.Height + Dp(2)),
            _kind == StatusKind.Working ? P.SecondaryText : P.Text, TextFormatFlags.WordBreak);
    }
}

internal sealed class FolderPicker : ThemedControl
{
    public FolderPicker(ModernTextBox box, ModernButton browse)
    {
        Box = box;
        Browse = browse;
        BackColor = P.Card;
        Controls.Add(box);
        Controls.Add(browse);
    }

    public ModernTextBox Box { get; }

    public ModernButton Browse { get; }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(34));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var button = Browse.GetPreferredSize(Size.Empty);
        Browse.SetBounds(Width - button.Width, 0, button.Width, Height);
        Box.SetBounds(0, 0, Math.Max(0, Width - button.Width - Dp(8)), Height);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Box.Invalidate();
        Browse.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);
}