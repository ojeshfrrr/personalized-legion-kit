using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

// A paused worker waits on a signal rather than waking to poll a flag.
public sealed class OptionalWorkGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _resumed = CreateSignal();
    public bool IsPaused { get { lock (_lock) return !_resumed.Task.IsCompleted; } }
    public OptionalWorkGate(bool paused = true) { if (!paused) _resumed.SetResult(); }
    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (paused && _resumed.Task.IsCompleted) _resumed = CreateSignal();
            else if (!paused) _resumed.TrySetResult();
        }
    }
    public async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            Task resumed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (_resumed.Task.IsCompleted) return;
                resumed = _resumed.Task;
            }
            await resumed.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
