using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderPollingRead
{
    public static async Task RunAsync(string metadataEvidence, string outputPath)
    {
        AppFlags.Initialize(["--read-only"]);
        if (File.Exists(outputPath)) throw new InvalidOperationException("Polling evidence already exists; refusing to overwrite it.");
        byte[]? request = null, response = null;
        try
        {
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var paths = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var success) && success.GetBoolean() &&
                            WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .Select(d => d.GetProperty("path").GetString()!).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("Expected one verified configuration interface.");
            await using var transport = WindowsMouseFeatureReadTransport.Open(paths[0]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            request = DeathAdderV3PollingProtocol.CreateReadRequest(1);
            response = await transport.ExchangeAsync(request, deadline.Token);
            var rate = DeathAdderV3PollingProtocol.ParseReadback(response, 1);
            var result = new { Kind = "live-polling-configuration-getter", Validated = true,
                Device = "1532:00B2", Command = "00/C0", ProfileId = 1, Hertz = (int)rate,
                Request = Convert.ToHexString(request), Response = Convert.ToHexString(response),
                EffectiveEventFrequencyMeasured = false, SettingWrites = 0, Retries = 0, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            var result = new { Kind = "live-polling-configuration-getter", Validated = false, ErrorType = ex.GetType().Name,
                Error = ex.Message, NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                Request = request is null ? null : Convert.ToHexString(request), Response = response is null ? null : Convert.ToHexString(response),
                SettingWrites = 0, Retries = 0, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Environment.ExitCode = 1;
        }
    }
}
