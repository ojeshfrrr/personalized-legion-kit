using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

// Independently implemented from the locally cached product-178 wire-format evidence.
// Class-1 DPI reads are live validated; class-0 active overrides await a controlled test.
public enum DeathAdderV3ReadCommand : byte
{
    CurrentDpi = 0x85,
    DpiStages = 0x86,
}

public sealed record MouseDpi(ushort X, ushort Y);
public sealed record MouseDpiStage(byte Id, MouseDpi Dpi);
public sealed record MouseDpiStages(byte ActiveStage, IReadOnlyList<MouseDpiStage> Stages);

public enum MouseDpiScope : byte { ActiveOverride = 0, Profile = 1 }

public static class DeathAdderV3Protocol
{
    public const ushort VendorId = 0x1532;
    public const ushort ProductId = 0x00B2;
    public const int FeatureReportLength = 91;
    public const int MinimumDpi = 100;
    public const int MaximumDpi = 30000;
    public const int DpiStep = 50;

    public static byte[] CreateReadRequest(DeathAdderV3ReadCommand command, byte transaction, MouseDpiScope scope = MouseDpiScope.Profile)
    {
        if (scope is not (MouseDpiScope.ActiveOverride or MouseDpiScope.Profile) ||
            (command == DeathAdderV3ReadCommand.DpiStages && scope != MouseDpiScope.Profile))
            throw new ArgumentOutOfRangeException(nameof(scope));
        if (transaction is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(transaction));
        var payloadLength = command switch
        {
            DeathAdderV3ReadCommand.CurrentDpi => 7,
            DeathAdderV3ReadCommand.DpiStages => 80,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var report = new byte[FeatureReportLength];
        report[2] = transaction;
        report[6] = (byte)payloadLength;
        report[7] = 0x04;
        report[8] = (byte)command;
        report[9] = (byte)scope;
        report[89] = Checksum(report);
        return report;
    }

    public static MouseDpi ParseCurrentDpi(ReadOnlySpan<byte> report, byte transaction, MouseDpiScope scope = MouseDpiScope.Profile)
    {
        var data = ValidateResponse(report, (byte)DeathAdderV3ReadCommand.CurrentDpi, transaction, scope);
        if (data.Length < 7)
            throw new InvalidDataException("Truncated current-DPI reply.");
        return ReadDpi(data[1..]);
    }

    public static MouseDpiStages ParseDpiStages(ReadOnlySpan<byte> report, byte transaction)
    {
        var data = ValidateResponse(report, (byte)DeathAdderV3ReadCommand.DpiStages, transaction, MouseDpiScope.Profile);
        if (data.Length < 3 || data[2] == 0 || data[2] > (data.Length - 3) / 7)
            throw new InvalidDataException("Invalid DPI stage count or truncated stage data.");
        var stages = new List<MouseDpiStage>(data[2]);
        var ids = new HashSet<byte>();
        for (var i = 0; i < data[2]; i++)
        {
            var offset = 3 + i * 7;
            var id = data[offset];
            if (id == 0 || !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate DPI stage identifier.");
            stages.Add(new MouseDpiStage(id, ReadDpi(data[(offset + 1)..])));
        }
        if (!ids.Contains(data[1]))
            throw new InvalidDataException("The active DPI stage is absent from the reply.");
        return new MouseDpiStages(data[1], stages.AsReadOnly());
    }

    public static MouseDpi ValidateDpi(int x, int y)
    {
        if (x < MinimumDpi || x > MaximumDpi || x % DpiStep != 0)
            throw new ArgumentOutOfRangeException(nameof(x), "X DPI must be 100–30000 in steps of 50.");
        if (y < MinimumDpi || y > MaximumDpi || y % DpiStep != 0)
            throw new ArgumentOutOfRangeException(nameof(y), "Y DPI must be 100–30000 in steps of 50.");
        return new MouseDpi((ushort)x, (ushort)y);
    }

    public static byte[] CreateActiveDpiRequest(int x, int y, byte transaction)
    {
        var dpi = ValidateDpi(x, y);
        var report = CreateReadRequest(DeathAdderV3ReadCommand.CurrentDpi, transaction, MouseDpiScope.ActiveOverride);
        report[8] = 0x05; // Active X/Y override; never the 0x06 saved-stage-table command.
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(10, 2), dpi.X);
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(12, 2), dpi.Y);
        report[89] = Checksum(report);
        return report;
    }

    public static void ValidateActiveDpiAcknowledgement(ReadOnlySpan<byte> report, byte transaction, MouseDpi requested)
    {
        var data = ValidateResponse(report, 0x05, transaction, MouseDpiScope.ActiveOverride);
        if (data.Length != 7 || ReadDpi(data[1..]) != requested || data[5] != 0 || data[6] != 0)
            throw new InvalidDataException("Active-DPI acknowledgement does not match the requested X/Y values.");
    }

    private static ReadOnlySpan<byte> ValidateResponse(ReadOnlySpan<byte> report, byte command, byte transaction, MouseDpiScope scope)
    {
        if (transaction is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(transaction));
        if (report.Length != FeatureReportLength || report[0] != 0 || report[90] != 0)
            throw new InvalidDataException("Unexpected HID report identifier, length, or trailer.");
        if (report[2] != transaction || report[7] != 0x04 || report[8] != command)
            throw new InvalidDataException("The HID reply belongs to a different request.");
        if (report[6] is < 1 or > 80 || report[89] != Checksum(report))
            throw new InvalidDataException("Invalid HID payload length or checksum.");
        if (report[1] == 5)
            throw new NotSupportedException("The device rejected this DPI operation as unsupported.");
        if (report[1] != 2)
            throw new IOException($"DPI operation failed with device status {report[1]}.");
        if (report[9] != (byte)scope)
            throw new InvalidDataException("Unexpected DPI class in device reply.");
        return report.Slice(9, report[6]);
    }

    private static MouseDpi ReadDpi(ReadOnlySpan<byte> data)
    {
        var x = BinaryPrimitives.ReadUInt16BigEndian(data);
        var y = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (x == 0 || y == 0)
            throw new InvalidDataException("Device returned zero DPI.");
        return new MouseDpi(x, y);
    }

    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (var i = 3; i <= 88; i++)
            checksum ^= report[i];
        return checksum;
    }
}
