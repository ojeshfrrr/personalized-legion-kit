using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public enum MouseButtonAction
{
    Disabled, LeftClick, RightClick, MiddleClick, Back, Forward, ScrollUp, ScrollDown,
    DoubleClick, DpiUp, DpiDown, DpiCycleUp, DpiCycleDown,
}
public enum MouseProfileQuery : byte { Count = 0x80, Ids = 0x81, Capacity = 0x8A }
public sealed record MouseButtonDefinition(byte Id, string Name);
public sealed record MouseProfileBanks(byte ReportedCapacity, IReadOnlyList<byte> Ids);
public sealed record MouseButtonAssignment(byte Profile, byte Button, byte Layer, byte Function, byte DataSize, string DataHex)
{
    public MouseButtonAction? KnownAction => DeathAdderV3ButtonProtocol.DecodeAction(Function, DataSize, DataHex);
    public MouseKeyboardAssignment? KnownKeyboard => MouseKeyboardAssignments.Decode(Function, DataSize, DataHex);
    public MouseButtonBinding? KnownBinding => KnownAction is { } action ? MouseButtonBinding.Basic(action)
        : KnownKeyboard is { } keyboard ? MouseButtonBinding.Key(keyboard) : null;
    public string Description => KnownBinding is { } binding ? binding.Description
        : $"Unsupported function 0x{Function:X2} · {DataHex} (read-only)";
}
public sealed record MouseButtonBank(byte Profile, IReadOnlyList<MouseButtonAssignment> Assignments);

/// <summary>Exact product-178 mouse OBM engine: ten-byte 02/8C and 02/0C payloads.</summary>
public static class DeathAdderV3ButtonProtocol
{
    public static IReadOnlyList<MouseButtonDefinition> Buttons { get; } = Array.AsReadOnly(new[]
    {
        new MouseButtonDefinition(1, "Left button"), new MouseButtonDefinition(2, "Right button"),
        new MouseButtonDefinition(3, "Wheel click"), new MouseButtonDefinition(4, "Side button 4"),
        new MouseButtonDefinition(5, "Side button 5"), new MouseButtonDefinition(9, "Wheel up"),
        new MouseButtonDefinition(10, "Wheel down"), new MouseButtonDefinition(96, "DPI button"),
    });
    public static IReadOnlyList<MouseButtonAction> Actions { get; } = Array.AsReadOnly(Enum.GetValues<MouseButtonAction>());
    public static string ActionLabel(MouseButtonAction action) => action switch
    {
        MouseButtonAction.Disabled => "Disabled", MouseButtonAction.LeftClick => "Left click",
        MouseButtonAction.RightClick => "Right click", MouseButtonAction.MiddleClick => "Wheel click",
        MouseButtonAction.Back => "Back", MouseButtonAction.Forward => "Forward",
        MouseButtonAction.ScrollUp => "Scroll up", MouseButtonAction.ScrollDown => "Scroll down",
        MouseButtonAction.DoubleClick => "Double left click", MouseButtonAction.DpiUp => "DPI stage up",
        MouseButtonAction.DpiDown => "DPI stage down", MouseButtonAction.DpiCycleUp => "DPI stage cycle up",
        MouseButtonAction.DpiCycleDown => "DPI stage cycle down", _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    private static (byte Function, byte Size, byte Data) Encode(MouseButtonAction action) => action switch
    {
        MouseButtonAction.Disabled => (0, 0, 0), MouseButtonAction.LeftClick => (1, 1, 1),
        MouseButtonAction.RightClick => (1, 1, 2), MouseButtonAction.MiddleClick => (1, 1, 3),
        MouseButtonAction.Back => (1, 1, 4), MouseButtonAction.Forward => (1, 1, 5),
        MouseButtonAction.ScrollUp => (1, 1, 9), MouseButtonAction.ScrollDown => (1, 1, 10),
        MouseButtonAction.DoubleClick => (11, 1, 1), MouseButtonAction.DpiUp => (6, 1, 1),
        MouseButtonAction.DpiDown => (6, 1, 2), MouseButtonAction.DpiCycleUp => (6, 1, 6),
        MouseButtonAction.DpiCycleDown => (6, 1, 7), _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    public static MouseButtonAction? DecodeAction(byte function, byte size, string dataHex)
    {
        foreach (var action in Actions)
        {
            var encoded = Encode(action);
            if (function == encoded.Function && size == encoded.Size &&
                dataHex == Convert.ToHexString(new byte[] { encoded.Data, 0, 0, 0, 0 })) return action;
        }
        return null;
    }
    public static void ValidateTarget(byte button, bool writing = false)
    {
        if (!Buttons.Any(b => b.Id == button)) throw new ArgumentOutOfRangeException(nameof(button));
        if (writing && button == 1) throw new InvalidOperationException("The primary left button is protected to preserve ordinary input.");
    }
    public static byte[] CreateProfileQuery(MouseProfileQuery query, byte transaction)
    {
        if (!Enum.IsDefined(query)) throw new ArgumentOutOfRangeException(nameof(query));
        return Frame(5, (byte)query, query == MouseProfileQuery.Ids ? (byte)80 : (byte)1, transaction);
    }
    public static byte[] CreateRead(byte profile, byte button, byte transaction)
    {
        ValidateTarget(button);
        var frame = Frame(2, 0x8C, 10, transaction); frame[9] = profile; frame[10] = button;
        frame[89] = Checksum(frame); return frame; // Normal layer 0 only.
    }
    public static byte[] CreateSet(byte button, MouseButtonAction action, byte transaction)
    {
        ValidateTarget(button, writing: true);
        var encoded = Encode(action);
        var frame = Frame(2, 0x0C, 10, transaction);
        frame[9] = 1; frame[10] = button; frame[12] = encoded.Function;
        frame[13] = encoded.Size; frame[14] = encoded.Data;
        frame[89] = Checksum(frame); return frame;
    }
    public static void ValidateGetter(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 91) throw new ArgumentException("Unexpected button/profile getter size.");
        var expected = frame[7] == 5 ? CreateProfileQuery((MouseProfileQuery)frame[8], frame[2])
            : CreateRead(frame[9], frame[10], frame[2]);
        if (!frame.SequenceEqual(expected)) throw new ArgumentException("Only exact normal-layer assignment/profile getters are permitted.");
    }
    public static void ValidateSetter(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 91) throw new ArgumentException("Unexpected assignment setter size.");
        if (frame[12] == 2)
        {
            var keyboard = MouseKeyboardAssignments.Decode(frame[12], frame[13], Convert.ToHexString(frame.Slice(14, 5)))
                ?? throw new NotSupportedException("Unsupported keyboard assignment or noncanonical payload.");
            if (!frame.SequenceEqual(CreateKeyboardSet(frame[10], keyboard, frame[2])))
                throw new ArgumentException("Only exact profile-1 normal-layer keyboard assignments are permitted.");
            return;
        }
        var action = DecodeAction(frame[12], frame[13], Convert.ToHexString(frame.Slice(14, 5)))
            ?? throw new NotSupportedException("Unsupported assignment function or noncanonical payload.");
        if (!frame.SequenceEqual(CreateSet(frame[10], action, frame[2])))
            throw new ArgumentException("Only exact profile-1 normal-layer button assignments are permitted.");
    }
    public static byte ParseScalar(ReadOnlySpan<byte> reply, MouseProfileQuery query, byte transaction)
    {
        if (query is not (MouseProfileQuery.Capacity or MouseProfileQuery.Count)) throw new ArgumentOutOfRangeException(nameof(query));
        ValidateReply(reply, 5, (byte)query, 1, transaction); return reply[9];
    }
    public static IReadOnlyList<byte> ParseIds(ReadOnlySpan<byte> reply, byte transaction)
    {
        // Exact Synapse parser slices actual returned data, not the 80-byte request.
        // This wired device returns length 2, advertised list limit 5, ID 1 when
        // count=1. Never invent the four absent IDs or treat capacity as count.
        if (reply.Length != 91 || reply[6] is < 1 or > 80) throw new InvalidDataException("Invalid profile-ID payload length.");
        ValidateReply(reply, 5, 0x81, reply[6], transaction);
        var count = reply[9];
        if (count > 79) throw new InvalidDataException("Profile ID count exceeds the payload.");
        var ids = reply.Slice(10, Math.Min(count, reply[6] - 1)).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Duplicate profile IDs.");
        return Array.AsReadOnly(ids);
    }
    public static MouseButtonAssignment ParseAssignment(ReadOnlySpan<byte> reply, byte profile, byte button, byte transaction, bool acknowledgement = false)
    {
        ValidateTarget(button);
        ValidateReply(reply, 2, acknowledgement ? (byte)0x0C : (byte)0x8C, 10, transaction);
        if (reply[9] != profile || reply[10] != button || reply[11] != 0 || reply[13] > 5)
            throw new InvalidDataException("Assignment reply has a different bank/button/layer or invalid data size.");
        return new(profile, button, 0, reply[12], reply[13], Convert.ToHexString(reply.Slice(14, 5)));
    }
    public static void ValidateAcknowledgement(ReadOnlySpan<byte> reply, byte button, MouseButtonAction action, byte transaction)
    {
        if (ParseAssignment(reply, 1, button, transaction, acknowledgement: true).KnownAction != action)
            throw new InvalidDataException("Assignment acknowledgement differs from the request.");
    }
    public static byte[] CreateKeyboardSet(byte button, MouseKeyboardAssignment assignment, byte transaction)
    {
        ValidateTarget(button, writing: true); MouseKeyboardAssignments.Validate(assignment);
        var frame = Frame(2, 0x0C, 10, transaction);
        frame[9] = 1; frame[10] = button; frame[12] = 2; frame[13] = 2;
        frame[14] = (byte)assignment.Modifiers; frame[15] = (byte)assignment.Key;
        frame[89] = Checksum(frame); return frame;
    }
    public static byte[] CreateBindingSet(byte button, MouseButtonBinding binding, byte transaction)
    {
        ArgumentNullException.ThrowIfNull(binding); binding.Validate();
        return binding.Action is { } action ? CreateSet(button, action, transaction) : CreateKeyboardSet(button, binding.Keyboard!, transaction);
    }
    public static void ValidateBindingAcknowledgement(ReadOnlySpan<byte> reply, byte button, MouseButtonBinding binding, byte transaction)
    {
        binding.Validate();
        if (binding.Action is { } action) { ValidateAcknowledgement(reply, button, action, transaction); return; }
        if (ParseAssignment(reply, 1, button, transaction, acknowledgement: true).KnownKeyboard != binding.Keyboard)
            throw new InvalidDataException("Keyboard acknowledgement differs from the requested key/modifiers.");
    }
    private static byte[] Frame(byte commandClass, byte command, byte size, byte transaction)
    {
        if (transaction is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(transaction));
        var result = new byte[91]; result[2] = transaction; result[6] = size; result[7] = commandClass; result[8] = command;
        result[89] = Checksum(result); return result;
    }
    private static void ValidateReply(ReadOnlySpan<byte> reply, byte commandClass, byte command, byte size, byte transaction)
    {
        if (transaction is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(transaction));
        if (reply.Length != 91 || reply[0] != 0 || reply[90] != 0 || reply[2] != transaction ||
            reply[3] != 0 || reply[4] != 0 || reply[5] != 0 || reply[6] != size || reply[7] != commandClass ||
            reply[8] != command || reply[89] != Checksum(reply)) throw new InvalidDataException("Malformed, stale or corrupt assignment/profile reply.");
        if (reply[1] == 5) throw new NotSupportedException("The device does not support this assignment/profile query.");
        if (reply[1] != 2) throw new IOException($"Assignment/profile command failed with status {reply[1]}.");
    }
    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0; for (var i = 3; i <= 88; i++) checksum ^= report[i]; return checksum;
    }
}
