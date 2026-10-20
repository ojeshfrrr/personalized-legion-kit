using System;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Utils;

public static class HardwareCommandRunner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(Func<Task> change)
    {
        HardwareRuntime.RequireControls();
        if (!await Gate.WaitAsync(0))
            throw new InvalidOperationException("Another hardware change is still pending. Wait for it to finish.");
        try
        {
            // Do not time out by abandoning an in-flight firmware write. The implementation owns its timeout.
            await change();
        }
        finally
        {
            Gate.Release();
        }
    }
}
