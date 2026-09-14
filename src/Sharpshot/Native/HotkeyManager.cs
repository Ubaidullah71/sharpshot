using Sharpshot.Settings;

namespace Sharpshot.Native;

internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private readonly HashSet<int> _registered = [];

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = NativeMethods.HWND_MESSAGE });
    }

    public event Action<int>? Pressed;

    /// <summary>Returns false if another app already owns this combination.</summary>
    public bool Register(int id, Hotkey hotkey)
    {
        Unregister(id);
        if (hotkey.IsEmpty)
        {
            return true;
        }

        var modifiers = (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers, (uint)hotkey.Key))
        {
            return false;
        }

        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.ToArray())
        {
            Unregister(id);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            Pressed?.Invoke((int)m.WParam);
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}