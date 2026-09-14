using Sharpshot.Native;
using Sharpshot.Settings;

namespace Sharpshot.UI;

internal sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey;

    public HotkeyBox()
    {
        ShortcutsEnabled = false;
        PlaceholderText = "Click here, then press keys";
        Cursor = Cursors.Arrow;
    }

    public event EventHandler? HotkeyChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            ShowCurrent();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;

        // Leave Tab / Shift+Tab alone so keyboard navigation still works.
        if (key == Keys.Tab && modifiers is Keys.None or Keys.Shift)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Record(key, modifiers);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        // Windows only sends key-up for Print Screen, so it never reaches ProcessCmdKey.
        if (e.KeyCode == Keys.PrintScreen)
        {
            Record(Keys.PrintScreen, e.Modifiers);
        }
        else if (Hotkey.IsModifierKey(e.KeyCode) && e.Modifiers == Keys.None)
        {
            ShowCurrent(); // all modifiers released without a key: undo the preview
        }
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        ShowCurrent();
    }

    private void Record(Keys key, Keys modifierKeys)
    {
        var modifiers = HotkeyModifiers.None;
        if (modifierKeys.HasFlag(Keys.Control)) modifiers |= HotkeyModifiers.Control;
        if (modifierKeys.HasFlag(Keys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (modifierKeys.HasFlag(Keys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN)) modifiers |= HotkeyModifiers.Windows;

        if (modifiers == HotkeyModifiers.None && key == Keys.Escape)
        {
            ShowCurrent(); // cancel whatever was being typed and keep the hotkey
            return;
        }

        if (modifiers == HotkeyModifiers.None && key is Keys.Back or Keys.Delete)
        {
            SetHotkey(Hotkey.None);
            return;
        }

        if (Hotkey.IsModifierKey(key))
        {
            Text = modifiers == HotkeyModifiers.None ? "" : ModifierPreview(modifiers);
            return;
        }

        var candidate = new Hotkey(modifiers, key);
        if (candidate.IsAllowed)
        {
            SetHotkey(candidate);
        }
        else
        {
            Text = $"{candidate.ToDisplayString()} (add Ctrl, Alt or Shift)";
        }
    }

    private static string ModifierPreview(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return string.Join(" + ", parts) + " + …";
    }

    private void SetHotkey(Hotkey hotkey)
    {
        var changed = hotkey != _hotkey;
        Hotkey = hotkey;
        SelectionStart = TextLength;
        if (changed)
        {
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowCurrent() => Text = _hotkey.IsEmpty ? "" : _hotkey.ToDisplayString();
}