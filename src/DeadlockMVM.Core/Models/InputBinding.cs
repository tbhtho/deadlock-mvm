namespace DeadlockMVM.Core.Models;

public enum InputBindingKind
{
    Keyboard,
    Mouse,
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>Serializable keyboard or mouse binding independent from WPF controls.</summary>
public readonly record struct InputBinding(InputBindingKind Kind, uint Code, InputModifiers Modifiers)
{
    private static readonly IReadOnlyDictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["CapsLock"] = 0x14, ["OemMinus"] = 0xBD, ["OemPlus"] = 0xBB,
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23,
        ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27,
        ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["LeftShift"] = 0xA0, ["RightShift"] = 0xA1,
        ["LeftCtrl"] = 0xA2, ["RightCtrl"] = 0xA3,
        ["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63,
        ["NumPad4"] = 0x64, ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67,
        ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
    };

    public bool IsValid =>
        Code != 0 && Enum.IsDefined(Kind) &&
        (Kind == InputBindingKind.Keyboard || Code is >= 3 and <= 7);

    public override string ToString()
    {
        if (!IsValid)
            return string.Empty;
        var parts = new List<string>();
        if (Modifiers.HasFlag(InputModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(InputModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(InputModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(InputModifiers.Windows)) parts.Add("Win");
        parts.Add(Kind == InputBindingKind.Mouse ? FormatMouse(Code) : FormatKey(Code));
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out InputBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = InputModifiers.None;
        string? keyPart = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= InputModifiers.Control; break;
                case "alt": modifiers |= InputModifiers.Alt; break;
                case "shift": modifiers |= InputModifiers.Shift; break;
                case "win": case "windows": modifiers |= InputModifiers.Windows; break;
                default:
                    if (keyPart is not null)
                        return false;
                    keyPart = part;
                    break;
            }
        }

        if (keyPart is null)
            return false;
        if (keyPart.Equals("WheelUp", StringComparison.OrdinalIgnoreCase) ||
            keyPart.Equals("WheelDown", StringComparison.OrdinalIgnoreCase))
        {
            binding = new InputBinding(
                InputBindingKind.Mouse,
                keyPart.Equals("WheelUp", StringComparison.OrdinalIgnoreCase) ? 6u : 7u,
                modifiers);
            return true;
        }
        if (keyPart.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(keyPart.AsSpan(5), out var mouse) && mouse is >= 3 and <= 7)
        {
            binding = new InputBinding(InputBindingKind.Mouse, mouse, modifiers);
            return true;
        }
        if (!TryParseKey(keyPart, out var key))
            return false;
        binding = new InputBinding(InputBindingKind.Keyboard, key, modifiers);
        return true;
    }

    private static bool TryParseKey(string value, out uint key)
    {
        key = 0;
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = character;
                return true;
            }
        }
        if (value.Length is 2 or 3 && value[0] is 'F' or 'f' &&
            int.TryParse(value.AsSpan(1), out var function) && function is >= 1 and <= 24)
        {
            key = checked((uint)(0x6F + function));
            return true;
        }
        if (value.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var virtualKey) && virtualKey is > 0 and <= 0xFF)
        {
            key = virtualKey;
            return true;
        }
        return NamedKeys.TryGetValue(value, out key);
    }

    private static string FormatKey(uint key)
    {
        if (key is >= 0x70 and <= 0x87)
            return $"F{key - 0x6F}";
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)key).ToString();
        var named = NamedKeys.FirstOrDefault(pair => pair.Value == key);
        return named.Key ?? $"VK{key:X2}";
    }

    private static string FormatMouse(uint code) => code switch
    {
        6 => "WheelUp",
        7 => "WheelDown",
        _ => $"Mouse{code}",
    };
}
