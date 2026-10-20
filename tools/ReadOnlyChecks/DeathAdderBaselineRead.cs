using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderBaselineRead
{
    // Baseline-only entry point. This type has no settings transport or write call.
    public static async Task RunAsync(string metadataEvidence, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var journal = Path.Combine(outputDirectory, "reports.jsonl");
        object? active = null, profile = null, stages = null;
        var phase = "open";
        AppFlags.Initialize(["--read-only"]);
        try
        {
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var matches = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var success) && success.GetBoolean() &&
                            WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Expected one successfully verified configuration interface.");
            await using var transport = WindowsMouseFeatureReadTransport.Open(matches[0].GetProperty("path").GetString()!);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            byte transaction = 0;
            async Task<byte[]> Read(DeathAdderV3ReadCommand command, MouseDpiScope scope)
            {
                transaction++;
                var request = DeathAdderV3Protocol.CreateReadRequest(command, transaction, scope);
                var response = await transport.ExchangeAsync(request, deadline.Token);
                await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new
                {
                    Utc = DateTime.UtcNow, Phase = phase, Request = Convert.ToHexString(request), Response = Convert.ToHexString(response),
                }) + Environment.NewLine);
                return response;
            }
            phase = "active-dpi-class-0";
            var response = await Read(DeathAdderV3ReadCommand.CurrentDpi, MouseDpiScope.ActiveOverride);
            var dpi = DeathAdderV3Protocol.ParseCurrentDpi(response, transaction, MouseDpiScope.ActiveOverride);
            active = dpi;
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "active.json"), JsonSerializer.Serialize(dpi));
            phase = "profile-dpi-class-1";
            response = await Read(DeathAdderV3ReadCommand.CurrentDpi, MouseDpiScope.Profile);
            profile = DeathAdderV3Protocol.ParseCurrentDpi(response, transaction);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "profile.json"), JsonSerializer.Serialize(profile));
            phase = "stages-class-1";
            response = await Read(DeathAdderV3ReadCommand.DpiStages, MouseDpiScope.Profile);
            stages = DeathAdderV3Protocol.ParseDpiStages(response, transaction);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "stages.json"), JsonSerializer.Serialize(stages));
            var result = new
            {
                Kind = "live-read-only-baseline", Complete = true, Active = active, Profile = profile, Stages = stages,
                ActiveMatchesRequired = dpi == new MouseDpi(1600, 1600), SettingWrites = 0,
                PhysicalWritePermittedByThisProcess = false, Utc = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            var result = new
            {
                Kind = "live-read-only-baseline", Complete = false, Phase = phase, Active = active, Profile = profile, Stages = stages,
                ErrorType = ex.GetType().Name, Error = ex.Message,
                NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                SettingWrites = 0, PhysicalWritePermittedByThisProcess = false, Utc = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Environment.ExitCode = 1;
        }
    }
}
