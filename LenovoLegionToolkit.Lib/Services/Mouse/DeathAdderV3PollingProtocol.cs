using System;
using System.Collections.Generic;
using System.IO;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public enum MousePollingRate { Hz125 = 125, Hz250 = 250, Hz500 = 500, Hz1000 = 1000, Hz2000 = 2000, Hz4000 = 4000, Hz8000 = 8000 }

/// <summary>
/// Product-178 high-speed USB polling configuration. Profile 1 is the exact local
/// middleware's default target; no volatile/active-only persistence claim is made.
/// </summary>
public static class DeathAdderV3PollingProtocol
{
    public const byte ProfileId = 1;
    public static IReadOnlyList<MousePollingRate> SupportedRates { get; } = Array.AsReadOnly(new[]
    {
        MousePollingRate.Hz125, MousePollingRate.Hz250, MousePollingRate.Hz500, MousePollingRate.Hz1000,
        MousePollingRate.Hz2000, MousePollingRate.Hz4000, MousePollingRate.Hz8000,
    });

    public static byte Encode(MousePollingRate rate) => rate switch
    {
        MousePollingRate.Hz8000 => 1, MousePollingRate.Hz4000 => 2, MousePollingRate.Hz2000 => 4,
        MousePollingRate.Hz1000 => 8, MousePollingRate.Hz500 => 16, MousePollingRate.Hz250 => 32,
        MousePollingRate.Hz125 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), "Unsupported DeathAdder V3 polling rate."),
    };

    public static MousePollingRate Decode(byte code) => code switch
    {
        1 => MousePollingRate.Hz8000, 2 => MousePollingRate.Hz4000, 4 => MousePollingRate.Hz2000,
        8 => MousePollingRate.Hz1000, 16 => MousePollingRate.Hz500, 32 => MousePollingRate.Hz250,
        64 => MousePollingRate.Hz125,
        _ => throw new InvalidDataException($"Unknown polling-rate code 0x{code:X2}; no rate can be reported."),
    };

    public static byte[] CreateReadRequest(byte transaction) => Create(0xC0, 0, transaction);
    public static byte[] CreateSetRequest(MousePollingRate rate, byte transaction) => Create(0x40, Encode(rate), transaction);

    private static byte[] Create(byte command, byte rate, byte transaction)
    {
        if (transaction is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(transaction));
        var report = new byte[DeathAdderV3Protocol.FeatureReportLength];
        report[2] = transaction;
        report[6] = 2;
        report[7] = 0;
        report[8] = command;
        report[9] = ProfileId;
        report[10] = rate;
        report[89] = Checksum(report);
        return report;
    }

    public static void ValidateReadRequest(ReadOnlySpan<byte> report)
    {
        if (report.Length != 91 || !report.SequenceEqual(CreateReadRequest(report[2])))
            throw new ArgumentException("Expected the exact 00/C0 profile-1 polling getter.", nameof(report));
    }

    public static void ValidateSetRequest(ReadOnlySpan<byte> report)
    {
        if (report.Length != 91) throw new ArgumentException("Unexpected polling report length.", nameof(report));
        if (!report.SequenceEqual(CreateSetRequest(Decode(report[10]), report[2])))
            throw new ArgumentException("Only exact profile-1 polling-rate reports are permitted.", nameof(report));
    }

    public static MousePollingRate ParseReadback(ReadOnlySpan<byte> report, byte transaction) => Parse(report, transaction, 0xC0);

    public static void ValidateAcknowledgement(ReadOnlySpan<byte> report, byte transaction, MousePollingRate requested)
    {
        _ = Encode(requested);
        if (Parse(report, transaction, 0x40) != requested)
            throw new InvalidDataException("Polling acknowledgement differs from the requested rate.");
    }

    private static MousePollingRate Parse(ReadOnlySpan<byte> report, byte transaction, byte command)
    {
        if (transaction is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(transaction));
        if (report.Length != 91 || report[0] != 0 || report[90] != 0 || report[6] != 2 ||
            report[2] != transaction || report[7] != 0 || report[8] != command || report[89] != Checksum(report))
            throw new InvalidDataException("Malformed, stale or corrupt polling-rate reply.");
        if (report[1] == 5) throw new NotSupportedException("The device rejected the high-speed polling operation.");
        if (report[1] != 2) throw new IOException($"Polling operation failed with device status {report[1]}.");
        if (report[9] != ProfileId) throw new InvalidDataException("Polling reply belongs to another profile.");
        return Decode(report[10]);
    }

    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte sum = 0;
        for (var i = 3; i <= 88; i++) sum ^= report[i];
        return sum;
    }
}
