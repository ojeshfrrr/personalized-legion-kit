using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

/// <summary>
/// Explicitly opened HID session. Only getters, permission-gated active X/Y overrides,
/// and the exact profile-1 high-speed polling-rate operation.
/// No input reads, initialization commands,
/// access escalation, retry policy, polling, or production startup registration.
/// </summary>
public sealed class WindowsMouseFeatureReadTransport : IMouseFeatureDpiTransport, IMouseFeaturePollingTransport, IMouseFeatureButtonTransport, IAsyncDisposable
{
    // Windows SDK hidclass.h: HID_IN/OUT_CTL_CODE(100), FILE_ANY_ACCESS.
    private const uint SetFeature = 0x000B0191;
    private const uint GetFeature = 0x000B0192;
    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private bool _disposed;

    private WindowsMouseFeatureReadTransport(SafeFileHandle handle) => _handle = handle;

    public static WindowsMouseFeatureReadTransport Open(string path)
    {
        if (!IsConfigurationPath(path))
            throw new ArgumentException("Expected the plain DeathAdder V3 MI_00 HID collection.", nameof(path));
        // Match Synapse's metadata-capable fallback, without first requesting input access.
        var handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "CreateFileW failed (access=0, share=3, OPEN_EXISTING, OVERLAPPED).");
        }
        try
        {
            var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
            if (!Native.HidD_GetAttributes(handle, ref attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetAttributes failed.");
            if (attributes.Vendor != DeathAdderV3Protocol.VendorId || attributes.Product != DeathAdderV3Protocol.ProductId)
                throw new NotSupportedException("The opened device does not match 1532:00B2.");
            if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData failed.");
            try
            {
                var caps = new byte[64];
                if (Native.HidP_GetCaps(preparsed, caps) != 0x00110000 ||
                    BitConverter.ToUInt16(caps, 0) != 2 || BitConverter.ToUInt16(caps, 2) != 1 ||
                    BitConverter.ToUInt16(caps, 8) != DeathAdderV3Protocol.FeatureReportLength)
                    throw new NotSupportedException("Unexpected HID usage or feature-report length.");
            }
            finally { Native.HidD_FreePreparsedData(preparsed); }
            return new WindowsMouseFeatureReadTransport(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public static bool IsConfigurationPath(string? path) => path is not null &&
        path.StartsWith(@"\\?\hid#vid_1532&pid_00b2&mi_00#", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(@"#{4d1e55b2-f16f-11cf-88cb-001111000030}", StringComparison.OrdinalIgnoreCase);

    public static void ValidateGetter(ReadOnlySpan<byte> report)
    {
        if (report.Length != DeathAdderV3Protocol.FeatureReportLength)
            throw new ArgumentException("Unexpected getter report length.", nameof(report));
        if (report[7] is 2 or 5)
        {
            DeathAdderV3ButtonProtocol.ValidateGetter(report);
            return;
        }
        if (report[7] == 0 && report[8] == 0xC0)
        {
            DeathAdderV3PollingProtocol.ValidateReadRequest(report);
            return;
        }
        var expected = DeathAdderV3Protocol.CreateReadRequest((DeathAdderV3ReadCommand)report[8], report[2], (MouseDpiScope)report[9]);
        if (!report.SequenceEqual(expected))
            throw new ArgumentException("Only exact, validated DPI getter reports may be transmitted.", nameof(report));
    }

    public static void ValidateActiveDpiRequest(ReadOnlySpan<byte> report)
    {
        if (report.Length != DeathAdderV3Protocol.FeatureReportLength)
            throw new ArgumentException("Unexpected active-DPI report length.", nameof(report));
        var expected = DeathAdderV3Protocol.CreateActiveDpiRequest(
            BinaryPrimitives.ReadUInt16BigEndian(report.Slice(10, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(report.Slice(12, 2)), report[2]);
        if (!report.SequenceEqual(expected))
            throw new ArgumentException("Only exact class-0 active-DPI overrides may be transmitted. Saved stages are not supported.", nameof(report));
    }

    public static void ValidateFeatureCompletion(ReadOnlySpan<byte> report, uint deviceBytes)
    {
        // For unnumbered reports Windows counts the 90 device bytes, not the
        // leading zero supplied by the caller. Local HID.node RVA 0x17C32 reads
        // bytes_returned, then increments it at 0x17C3B before returning to JS.
        if (report.Length != DeathAdderV3Protocol.FeatureReportLength || report[0] != 0 ||
            deviceBytes != DeathAdderV3Protocol.FeatureReportLength - 1)
            throw new InvalidDataException($"Unexpected HID completion: buffer={report.Length}, device bytes={deviceBytes}.");
    }

    public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> getterReport, CancellationToken token)
    {
        // Own the buffer and reject modifications/settings commands before opening any I/O.
        var request = getterReport.ToArray();
        ValidateGetter(request);
        return ExchangeCoreAsync(request, false, token);
    }

    public Task<byte[]> ExchangeActiveDpiAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        MouseControlService.RequireMouseWritePermission();
        var request = report.ToArray();
        ValidateActiveDpiRequest(request);
        return ExchangeCoreAsync(request, true, token);
    }

    public Task<byte[]> ExchangePollingRateAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        MouseControlService.RequireMouseWritePermission();
        var request = report.ToArray();
        DeathAdderV3PollingProtocol.ValidateSetRequest(request);
        return ExchangeCoreAsync(request, true, token);
    }

    public Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        MouseControlService.RequireMouseWritePermission();
        var request = report.ToArray();
        DeathAdderV3ButtonProtocol.ValidateSetter(request);
        return ExchangeCoreAsync(request, true, token);
    }

    private async Task<byte[]> ExchangeCoreAsync(byte[] request, bool writing, CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (writing) MouseControlService.RequireMouseWritePermission();
                Transfer(SetFeature, request, true, token);
                await Task.Delay(5, token).ConfigureAwait(false); // Exact middleware OUT/IN delay.
                var reply = new byte[DeathAdderV3Protocol.FeatureReportLength];
                var bytes = Transfer(GetFeature, reply, false, token);
                ValidateFeatureCompletion(reply, bytes);
                return reply;
            }, token).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    private uint Transfer(uint code, byte[] buffer, bool input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var completion = new EventWaitHandle(false, EventResetMode.ManualReset);
        var data = Marshal.AllocHGlobal(buffer.Length);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
        try
        {
            Marshal.Copy(buffer, 0, data, buffer.Length);
            Marshal.StructureToPtr(new Overlapped { Event = completion.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            var ok = Native.DeviceIoControl(_handle, code, input ? data : IntPtr.Zero,
                input ? (uint)buffer.Length : 0, input ? IntPtr.Zero : data,
                input ? 0 : (uint)buffer.Length, out var bytes, overlapped);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error != 997) // ERROR_IO_PENDING
                throw new Win32Exception(error, "HID feature DeviceIoControl failed.");
            if (!ok)
            {
                if (WaitHandle.WaitAny([completion, token.WaitHandle]) == 1)
                {
                    Native.CancelIoEx(_handle, overlapped);
                    // Cancellation is a request: buffers/event/handle stay alive until I/O drains.
                    // A malfunctioning kernel driver can delay completion; no unsafe forced free.
                    Native.GetOverlappedResult(_handle, overlapped, out _, true);
                    token.ThrowIfCancellationRequested();
                }
                if (!Native.GetOverlappedResult(_handle, overlapped, out bytes, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "HID feature completion failed.");
            }
            token.ThrowIfCancellationRequested();
            if (!input) Marshal.Copy(data, buffer, 0, buffer.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(data);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
        finally { _operation.Release(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        public int Size;
        public ushort Vendor, Product, Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public UIntPtr Internal, InternalHigh;
        public ulong Offset;
        public IntPtr Event;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr data, [Out] byte[] caps);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint bytes, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
