using System.Globalization;

namespace Clipboard.Windows.Platform;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

internal readonly record struct HotkeyChord(HotkeyModifiers Modifiers, ushort VirtualKey)
{
    private const ushort Tab = 0x09;
    private const ushort Escape = 0x1B;
    private const ushort Delete = 0x2E;
    private const ushort F4 = 0x73;

    public static HotkeyChord Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("A hotkey chord is required.");
        }

        string[] parts = value.Split(
            '+',
            StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrEmpty))
        {
            throw new FormatException("A hotkey must include a modifier and a key.");
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            HotkeyModifiers modifier = ParseModifier(parts[index]);
            if ((modifiers & modifier) != 0)
            {
                throw new FormatException("A hotkey cannot repeat a modifier.");
            }
            modifiers |= modifier;
        }

        ushort virtualKey = ParseKey(parts[^1]);
        Validate(modifiers, virtualKey);
        return new HotkeyChord(modifiers, virtualKey);
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }
        if ((Modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((Modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }
        if ((Modifiers & HotkeyModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static HotkeyModifiers ParseModifier(string value) =>
        value.ToUpperInvariant() switch
        {
            "ALT" => HotkeyModifiers.Alt,
            "CTRL" or "CONTROL" => HotkeyModifiers.Control,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
            _ => throw new FormatException(
                $"Unknown hotkey modifier '{value}'."),
        };

    private static ushort ParseKey(string value)
    {
        if (value.Length == 1)
        {
            char key = char.ToUpperInvariant(value[0]);
            if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return key;
            }
        }
        return value.ToUpperInvariant() switch
        {
            "TAB" => Tab,
            "ESC" or "ESCAPE" => Escape,
            "DELETE" or "DEL" => Delete,
            "F4" => F4,
            _ => throw new FormatException(
                $"Unknown hotkey key '{value}'."),
        };
    }

    private static void Validate(HotkeyModifiers modifiers, ushort virtualKey)
    {
        HotkeyModifiers usable = HotkeyModifiers.Alt
            | HotkeyModifiers.Control
            | HotkeyModifiers.Shift;
        if (modifiers == HotkeyModifiers.None
            || (modifiers & ~usable) != 0)
        {
            throw new FormatException("Windows-reserved or modifier-free hotkeys are not allowed.");
        }
        if (modifiers == HotkeyModifiers.Alt && virtualKey == Tab
            || modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt)
                && virtualKey == Delete
            || modifiers == HotkeyModifiers.Control && virtualKey == Escape
            || modifiers == HotkeyModifiers.Alt && virtualKey == F4)
        {
            throw new FormatException("This hotkey is reserved by Windows.");
        }
    }

    private static string KeyName(ushort virtualKey) =>
        virtualKey switch
        {
            Tab => "Tab",
            Escape => "Escape",
            Delete => "Delete",
            F4 => "F4",
            _ when virtualKey is >= 'A' and <= 'Z' =>
                ((char)virtualKey).ToString(CultureInfo.InvariantCulture),
            _ when virtualKey is >= '0' and <= '9' =>
                ((char)virtualKey).ToString(CultureInfo.InvariantCulture),
            _ => $"0x{virtualKey:X2}",
        };
}
