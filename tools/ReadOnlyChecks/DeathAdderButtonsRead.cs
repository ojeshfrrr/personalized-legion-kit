using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderButtonsRead
{
    // Getter-only diagnostic; the transport deliberately cannot send assignment setters.
    public static async Task RunAsync(string metadataEvidence, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using (new FileStream(Path.Combine(outputDirectory, "one-shot.started"), FileMode.CreateNew, FileAccess.Write)) { }
        AppFlags.Initialize(["--read-only"]);
        MouseProfileBanks? banks = null;
        try
        {
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var paths = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var ok) && ok.GetBoolean() &&
                    WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .Select(d => d.GetProperty("path").GetString()!).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("Expected one verified configuration interface.");
            await using var native = WindowsMouseFeatureReadTransport.Open(paths[0]);
            var readOnly = new JournalTransport(native, Path.Combine(outputDirectory, "reports.jsonl"));
            var client = new DeathAdderV3ButtonClient(readOnly, TimeSpan.FromSeconds(10));
            banks = await client.ReadProfilesAsync();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "banks.json"), JsonSerializer.Serialize(banks));
            if (!banks.Ids.Contains((byte)1)) throw new NotSupportedException("Bank 1 not reported; no mapping request sent.");
            var bank = await client.ReadBankAsync(1);
            var result = new { Complete = true, Device = "1532:00B2", Banks = banks, Bank = bank,
                SettingWrites = 0, ProfileWrites = 0, Retries = 0, OnboardPersistenceVerified = false, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            var result = new { Complete = false, Banks = banks, ErrorType = ex.GetType().Name, Error = ex.Message,
                NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                SettingWrites = 0, ProfileWrites = 0, Retries = 0, Stopped = true, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result)); Environment.ExitCode = 1;
        }
    }
    private sealed class JournalTransport(IMouseFeatureReadTransport native, string journal) : IMouseFeatureButtonTransport
    {
        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken token)
        {
            DeathAdderV3ButtonProtocol.ValidateGetter(request.Span);
            var response = await native.ExchangeAsync(request, token);
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { Utc = DateTime.UtcNow,
                Request = Convert.ToHexString(request.Span), Response = Convert.ToHexString(response), SettingWrite = false }) + Environment.NewLine);
            return response;
        }
        public Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token) =>
            throw new InvalidOperationException("This getter-only diagnostic cannot send assignment writes.");
    }
}
