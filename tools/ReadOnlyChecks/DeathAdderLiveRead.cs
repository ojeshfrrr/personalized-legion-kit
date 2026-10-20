using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderLiveRead
{
    // Explicit diagnostic entry point only. Normal assertion runs never enter this path.
    public static async Task RunAsync(string metadataEvidence)
    {
        AppFlags.Initialize(["--read-only"]);
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var matches = document.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var success) && success.GetBoolean() &&
                            WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("A unique, successfully opened MI_00 metadata record is required.");
            var path = matches[0].GetProperty("path").GetString()!;
            await using var transport = WindowsMouseFeatureReadTransport.Open(path);
            var client = new DeathAdderV3ReadClient(0x1532, 0x00B2, transport, TimeSpan.FromSeconds(2));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var dpi = await client.ReadCurrentDpiAsync(cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = "live-device-getter", Device = "1532:00B2", Interface = "MI_00",
                Getter = "04/85", Validated = true, DpiX = dpi.X, DpiY = dpi.Y,
                SettingCommandsSent = 0, Retries = 0, ReadOnly = true,
            }));
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = "live-device-getter", Validated = false, ErrorType = ex.GetType().Name,
                Error = ex.Message, NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                SettingCommandsSent = 0, Retries = 0, ReadOnly = true,
            }));
            Environment.ExitCode = 1;
        }
    }
}
