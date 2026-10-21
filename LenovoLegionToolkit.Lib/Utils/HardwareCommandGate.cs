using System;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

// Shared by desktop commands and automation; never abandon an in-flight firmware write.
public static class HardwareCommandGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task RunAsync(Func<Task> change, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        HardwareRuntime.RequireControls();
        if (!await Gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Another hardware change is still pending. Wait for it to finish.");
        try
        {
            token.ThrowIfCancellationRequested();
            HardwareRuntime.RequireControls();
            await change().ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
}
