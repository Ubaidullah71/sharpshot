using System.Drawing.Drawing2D;
using Sharpshot.Native;

namespace Sharpshot.UI;

/// <summary>Dropdown menus in the settings window. The tray uses <see cref="PopupMenu"/>.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer()
    {
        RoundedEdges = false;
    }

    private static Palette P => Theme.Current;

    /// <summary>Takes the font and DPI from <paramref name="owner"/>.</summary>
    public static void Style(ToolStripDropDownMenu menu, Control owner, bool checkMargin = false)
    {
        menu.Renderer = new MenuRenderer();
        menu.ShowImageMargin = !checkMargin;
        menu.ShowCheckMargin = checkMargin;
        menu.Font = owner.Font;
        menu.Padding = new Padding(owner.LogicalToDeviceUnits(4), owner.LogicalToDeviceUnits(6), owner.LogicalToDeviceUnits(4), owner.LogicalToDeviceUnits(6));
        menu.DropShadowEnabled = !NativeMethods.SystemRoundsCorners;
        menu.HandleCreated += (_, _) =>
        {
            NativeMethods.SetDwmInt(menu.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
            NativeMethods.SetDwmInt(menu.Handle, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.ToColorRef(P.MenuBorder));
        };
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(P.Menu);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (NativeMethods.SystemRoundsCorners && e.ToolStrip is ToolStripDropDown)
        {
            return;
        }

        using var pen = new Pen(P.MenuBorder);
        var size = e.ToolStrip.Size;
        e.Graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No separate image column.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var radius = Scale(e.Item, 4);
        var bounds = new RectangleF(Scale(e.Item, 2), 1, e.Item.Width - Scale(e.Item, 4), e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(e.Graphics, P.MenuHover, bounds, radius);
    }

    /// <summary>The selected option in a dropdown gets a small accent pill instead of a tick.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var width = Scale(e.Item, 3);
        var height = Scale(e.Item, 16);
        var pill = new RectangleF(Scale(e.Item, 6), (e.Item.Height - height) / 2f, width, height);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(e.Graphics, P.Accent, pill, width / 2f);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? P.Text : P.DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var inset = Scale(e.Item, 6);
        var y = e.Item.Height / 2;
        using var pen = new Pen(P.MenuBorder);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = P.SecondaryText;
        base.OnRenderArrow(e);
    }

    private static int Scale(ToolStripItem item, int logical) => item.Owner?.LogicalToDeviceUnits(logical) ?? logical;
}