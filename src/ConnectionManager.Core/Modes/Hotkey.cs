using System.Globalization;

namespace ConnectionManager.Core.Modes;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8 }

/// A global hotkey such as "Ctrl+Alt+G": the modifier flags and virtual-key code that RegisterHotKey expects.
public sealed record Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint F1 = 0x70;

    /// Accepts Ctrl/Control, Alt, Shift and Win, each at most once, then one letter, digit or F1–F24.
    /// A letter or digit needs Ctrl, Alt or Win: alone or with Shift it would swallow ordinary typing.
    public static bool TryParse(string? text, out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return false;

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag)) return false;
            modifiers |= flag;
        }

        if (KeyCode(parts[^1].ToUpperInvariant()) is not { } key) return false;
        if (key < F1 && (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return false;
        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    private static uint? KeyCode(string key)
    {
        if (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return key[0];
        if (key.Length is 2 or 3 && key[0] == 'F' && key[1] != '0'
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
            return F1 + (uint)n - 1;
        return null;
    }
}
