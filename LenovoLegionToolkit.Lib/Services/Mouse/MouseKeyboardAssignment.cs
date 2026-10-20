using System;
using System.Collections.Generic;
using System.Linq;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public enum MouseKeyboardKey : ushort
{
    A = 4, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit1 = 30, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0,
    Enter = 40, Escape, Backspace, Tab, Space,
    F1 = 58, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}
[Flags]
public enum MouseKeyboardModifiers : ushort
{
    None = 0, LeftCtrl = 1, LeftShift = 2, LeftAlt = 4, LeftWin = 8,
    RightCtrl = 16, RightShift = 32, RightAlt = 64, RightWin = 128,
}
public sealed record MouseKeyboardAssignment(MouseKeyboardKey Key, MouseKeyboardModifiers Modifiers = MouseKeyboardModifiers.None)
{
    public string Description => string.Join(" + ", Enum.GetValues<MouseKeyboardModifiers>()
        .Where(m => m != MouseKeyboardModifiers.None && Modifiers.HasFlag(m)).Select(m => m.ToString())
        .Append(Key.ToString().Replace("Digit", "", StringComparison.Ordinal)));
}

/// <summary>One supported key, optional USB modifier bits; not a macro or a multi-key chord.</summary>
public static class MouseKeyboardAssignments
{
    public static IReadOnlyList<MouseKeyboardKey> Keys { get; } = Array.AsReadOnly(Enum.GetValues<MouseKeyboardKey>());
    public static void Validate(MouseKeyboardAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!Enum.IsDefined(assignment.Key)) throw new ArgumentOutOfRangeException(nameof(assignment), "Unsupported keyboard HID usage.");
        if (((ushort)assignment.Modifiers & ~0xFF) != 0) throw new ArgumentOutOfRangeException(nameof(assignment), "Unknown keyboard modifier bits.");
    }
    public static MouseKeyboardAssignment? Decode(byte function, byte size, string dataHex)
    {
        if (function != 2 || size != 2 || dataHex.Length != 10) return null;
        byte[] data;
        try { data = Convert.FromHexString(dataHex); } catch (FormatException) { return null; }
        if (data[2] != 0 || data[3] != 0 || data[4] != 0 || !Enum.IsDefined((MouseKeyboardKey)data[1])) return null;
        return new((MouseKeyboardKey)data[1], (MouseKeyboardModifiers)data[0]);
    }
}

public sealed record MouseButtonBinding(MouseButtonAction? Action, MouseKeyboardAssignment? Keyboard)
{
    public static MouseButtonBinding Basic(MouseButtonAction action) => new(action, null);
    public static MouseButtonBinding Key(MouseKeyboardAssignment keyboard) => new(null, keyboard);
    public string Description => Action is { } action ? DeathAdderV3ButtonProtocol.ActionLabel(action) : Keyboard?.Description ?? "Unsupported";
    public void Validate()
    {
        if (Action.HasValue == (Keyboard is not null)) throw new ArgumentException("Choose exactly one basic or keyboard assignment.");
        if (Action is { } action) _ = DeathAdderV3ButtonProtocol.ActionLabel(action);
        else MouseKeyboardAssignments.Validate(Keyboard!);
    }
}
