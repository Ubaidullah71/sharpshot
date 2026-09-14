using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpshot.Settings;

/// <summary>Values match the Win32 MOD_* flags used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

[JsonConverter(typeof(HotkeyJsonConverter))]
public readonly record struct Hotkey(HotkeyModifiers Modifiers, Keys Key)
{
    private static readonly Dictionary<Keys, string> StoredNames = new()
    {
        [Keys.PrintScreen] = "PrintScreen",
        [Keys.Enter] = "Enter",
        [Keys.PageUp] = "PageUp",
        [Keys.PageDown] = "PageDown",
        [Keys.CapsLock] = "CapsLock",
        [Keys.Escape] = "Escape",
        [Keys.Back] = "Backspace",
    };

    public static Hotkey None => default;

    public bool IsEmpty => Key == Keys.None;

    /// <summary>A key on its own (no modifiers) is only allowed for keys nobody types with.</summary>
    public bool IsAllowed => !IsEmpty && !IsModifierKey(Key) && (Modifiers != HotkeyModifiers.None || CanStandAlone(Key));

    public static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    public static bool CanStandAlone(Keys key) => key is Keys.PrintScreen or Keys.Pause or Keys.Scroll or (>= Keys.F13 and <= Keys.F24);

    /// <summary>UI names for keys whose enum names are unreadable (Oem4 etc.).</summary>
    private static readonly Dictionary<Keys, string> DisplayNames = new()
    {
        [Keys.PrintScreen] = "PrtScn",
        [Keys.Enter] = "Enter",
        [Keys.Escape] = "Esc",
        [Keys.Back] = "Backspace",
        [Keys.Delete] = "Del",
        [Keys.Insert] = "Ins",
        [Keys.PageUp] = "PgUp",
        [Keys.PageDown] = "PgDn",
        [Keys.CapsLock] = "Caps Lock",
        [Keys.Scroll] = "Scroll Lock",
        [Keys.OemOpenBrackets] = "[",
        [Keys.OemCloseBrackets] = "]",
        [Keys.OemPipe] = "\\",
        [Keys.OemBackslash] = "\\",
        [Keys.OemSemicolon] = ";",
        [Keys.OemQuotes] = "'",
        [Keys.Oemcomma] = ",",
        [Keys.OemPeriod] = ".",
        [Keys.OemQuestion] = "/",
        [Keys.Oemtilde] = "`",
        [Keys.OemMinus] = "-",
        [Keys.Oemplus] = "=",
        [Keys.Multiply] = "Num *",
        [Keys.Add] = "Num +",
        [Keys.Subtract] = "Num -",
        [Keys.Divide] = "Num /",
        [Keys.Decimal] = "Num .",
    };

    /// <summary>"Ctrl+Shift+PrintScreen", the format stored in settings.json.</summary>
    public override string ToString() => Format("+", KeyName);

    /// <summary>"Ctrl + Shift + PrtScn", for the UI.</summary>
    public string ToDisplayString() => IsEmpty ? "None" : Format(" + ", DisplayName);

    /// <summary>"Ctrl+Shift+PrtScn", for menus.</summary>
    public string ToShortcutString() => IsEmpty ? "" : Format("+", DisplayName);

    internal static string DisplayName(Keys key) => key switch
    {
        _ when DisplayNames.TryGetValue(key, out var name) => name,
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num {key - Keys.NumPad0}",
        _ when !Enum.IsDefined(key) => $"Key {(int)key:X2}",
        _ => KeyName(key),
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Windows; break;
                default: return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            return false;
        }

        var candidate = new Hotkey(modifiers, key);
        if (!candidate.IsAllowed)
        {
            return false;
        }

        hotkey = candidate;
        return true;
    }

    private string Format(string separator, Func<Keys, string> keyName)
    {
        if (IsEmpty)
        {
            return "";
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(keyName(Key));
        return string.Join(separator, parts);
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        _ when StoredNames.TryGetValue(key, out var name) => name,
        // Some real keys (like the extra keys on Brazilian and Japanese keyboards) have no name: store their code.
        _ when !Enum.IsDefined(key) => $"0x{(int)key:X2}",
        _ => key.ToString(),
    };

    private static bool TryParseKey(string text, out Keys key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Keys.D0 + (text[0] - '0');
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)
            && code is > 0 and < 0xFF)
        {
            key = (Keys)code;
            return true;
        }

        foreach (var (candidate, name) in StoredNames)
        {
            if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                return true;
            }
        }

        if (Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(key) && (key & Keys.Modifiers) == 0 && key != Keys.None)
        {
            return true;
        }

        key = Keys.None;
        return false;
    }
}

internal sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    /// <summary>An unreadable hotkey is dropped (and logged) rather than making the whole settings file unreadable.</summary>
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Hotkey.TryParse(reader.GetString(), out var hotkey))
        {
            return hotkey;
        }

        reader.Skip();
        Log.Warn("Couldn't read a hotkey in settings.json; cleared it");
        return Hotkey.None;
    }

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsEmpty ? "None" : value.ToString());
}