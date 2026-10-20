using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

var pid = int.Parse(args[0]);
var seconds = int.Parse(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
try
{
    var client = new DiagnosticsClient(pid);
    using var session = client.StartEventPipeSession(new[] {
        new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
        new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
    }, requestRundown: true, circularBufferMB: 32);
    var trace = Path.Combine(output, "cpu.nettrace");
    using (var file = File.Create(trace))
    {
        var copy = session.EventStream.CopyToAsync(file);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        session.Stop();
        await copy;
    }
    var etlx = TraceLog.CreateFromEventPipeDataFile(trace);
    using var log = new TraceLog(etlx);
    var inclusive = new Dictionary<string, int>();
    var sampleTypes = new Dictionary<string, int>();
    var samples = 0;
    foreach (var evt in log.Events)
    {
        if (evt.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
        samples++;
        var type = evt.PayloadNames.Length == 0 ? "unknown" : evt.PayloadValue(0)?.ToString() ?? "null";
        sampleTypes[type] = sampleTypes.GetValueOrDefault(type) + 1;
        var frames = new HashSet<string>();
        var stack = evt.CallStack();
        for (var depth = 0; stack is not null && depth < 100; depth++, stack = stack.Caller)
            frames.Add(stack.CodeAddress.FullMethodName);
        foreach (var frame in frames) inclusive[frame] = inclusive.GetValueOrDefault(frame) + 1;
    }
    File.WriteAllText(Path.Combine(output, "stacks.json"), JsonSerializer.Serialize(new {
        ProcessId = pid, DurationSeconds = seconds, Samples = samples, SampleTypes = sampleTypes,
        Note = "Inclusive sample counts are not exclusive CPU percentages. Blocking/native frames and profiler overhead require interpretation.",
        Frames = inclusive.OrderByDescending(kv => kv.Value).Take(100).Select(kv => new { Frame = kv.Key, Count = kv.Value })
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Collected {samples} samples into {output}");
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
    Console.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
