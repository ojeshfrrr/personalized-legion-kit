using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

/// <summary>One shared owner for on-demand mouse sessions. Construction performs no I/O.</summary>
public sealed partial class MouseControlService : IMouseControlService, IMouseButtonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static void RequireMouseWritePermission()
    {
        if (AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview || Folders.IsReadOnlySession)
            throw new InvalidOperationException("Mouse settings are locked in this read-only session.");
        HardwareRuntime.RequireControls();
    }

    public async Task<MouseDpi> ReadActiveDpiAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await using var transport = OpenUniqueDevice();
                return await new DeathAdderV3DpiClient(transport, TimeSpan.FromSeconds(3)).ReadActiveDpiAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MouseDpiApplyResult> ApplyActiveDpiAsync(int x, int y, CancellationToken token = default)
    {
        RequireMouseWritePermission();
        var requested = DeathAdderV3Protocol.ValidateDpi(x, y); // Before discovery or transport access.
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transactionStarted = false;
        try
        {
            RequireMouseWritePermission();
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                RequireMouseWritePermission();
                await using var transport = OpenUniqueDevice();
                transactionStarted = true;
                return await new DeathAdderV3DpiClient(transport, TimeSpan.FromSeconds(5)).ApplyActiveDpiAsync(x, y, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Transaction errors after a possible write are returned by the client as Uncertain.
            return new(transactionStarted ? MouseDpiApplyStatus.Uncertain : MouseDpiApplyStatus.Failed,
                requested, null, null, transactionStarted, ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<MousePollingRate> ReadPollingRateAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await using var transport = OpenUniqueDevice();
                return await new DeathAdderV3PollingClient(transport, TimeSpan.FromSeconds(3)).ReadAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MousePollingApplyResult> ApplyPollingRateAsync(MousePollingRate rate, CancellationToken token = default)
    {
        RequireMouseWritePermission();
        _ = DeathAdderV3PollingProtocol.Encode(rate);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transactionStarted = false;
        try
        {
            RequireMouseWritePermission();
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                RequireMouseWritePermission();
                await using var transport = OpenUniqueDevice();
                transactionStarted = true;
                return await new DeathAdderV3PollingClient(transport, TimeSpan.FromSeconds(5)).ApplyAsync(rate, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(transactionStarted ? MousePollingApplyStatus.Uncertain : MousePollingApplyStatus.Failed,
                rate, null, null, transactionStarted, ex.Message);
        }
        finally { _gate.Release(); }
    }

    private static WindowsMouseFeatureReadTransport OpenUniqueDevice()
    {
        var paths = EnumerateConfigurationPaths();
        if (paths.Count != 1)
            throw new InvalidOperationException(paths.Count == 0
                ? "DeathAdder V3 wired is not connected or its configuration interface is unavailable."
                : "More than one DeathAdder V3 is connected. No device was selected.");
        return WindowsMouseFeatureReadTransport.Open(paths[0]);
    }

    private static unsafe IReadOnlyList<string> EnumerateConfigurationPaths()
    {
        PInvoke.HidD_GetHidGuid(out var guid);
        using var devices = PInvoke.SetupDiGetClassDevs(guid, null, HWND.Null,
            SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);
        if (devices.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!PInvoke.SetupDiEnumDeviceInterfaces(devices, null, guid, index, ref data))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error);
            }
            uint size = 0;
            _ = PInvoke.SetupDiGetDeviceInterfaceDetail(new HDEVINFO(devices.DangerousGetHandle()), &data, null, 0, &size, null);
            if (size is < 8 or > 65536) throw new InvalidOperationException("Unexpected HID interface detail size.");
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA_W*)memory;
                detail->cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();
                if (!PInvoke.SetupDiGetDeviceInterfaceDetail(new HDEVINFO(devices.DangerousGetHandle()), &data, detail, size, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                string path;
                fixed (char* first = &detail->DevicePath.e0) path = new string(first);
                if (WindowsMouseFeatureReadTransport.IsConfigurationPath(path)) paths.Add(path);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return paths.ToArray();
    }
}
