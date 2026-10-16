namespace Shiyu.Core;

[Flags]
public enum HotkeyModifier
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A hotkey as the user writes it and as the settings file stores it.
///
/// In Core rather than the platform layer because parsing and formatting it is
/// ordinary string handling that wants testing, and because the settings file
/// has to round-trip it without Windows being involved.
/// </summary>
public sealed record HotkeySpec(HotkeyModifier Modifiers, char Key)
{
    /// <summary>
    /// Parses "Ctrl+Shift+Z" and its variations, or returns null. Null rather
    /// than throwing: this comes from a text box and from a file a user may
    /// have edited by hand, and neither should be able to stop Shiyu starting.
    /// </summary>
    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var modifiers = HotkeyModifier.None;
        char? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifier.Control;
                    break;
                case "shift":
                    modifiers |= HotkeyModifier.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifier.Alt;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifier.Windows;
                    break;
                default:
                    if (part.Length != 1 || !char.IsLetterOrDigit(part[0]))
                    {
                        return null;
                    }

                    // A second key means the user typed something like
                    // "Ctrl+A+B", which is not a hotkey.
                    if (key is not null)
                    {
                        return null;
                    }

                    key = char.ToUpperInvariant(part[0]);
                    break;
            }
        }

        // A bare letter is not a global hotkey — registering "Z" alone would
        // take that key away from every application on the machine.
        return key is { } found && modifiers != HotkeyModifier.None
            ? new HotkeySpec(modifiers, found)
            : null;
    }

    /// <summary>
    /// Always in the same order, so the settings file stays stable. Win comes
    /// first, the way Windows itself writes its shortcuts ("Win+Alt+B") — the
    /// defaults became Win+Alt combinations (评审 2026-10-16 F-04).
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>();

        if (Modifiers.HasFlag(HotkeyModifier.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifier.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifier.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifier.Alt)) parts.Add("Alt");

        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
