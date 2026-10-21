namespace LenovoLegionToolkit.Lib.Utils;

// Event deduplication only; no timer, device access, persistence or startup action.
public sealed class PowerSourceTransitions
{
    private readonly object _gate = new();
    private PowerAdapterStatus? _last;
    private int _generation;
    public void Prime(PowerAdapterStatus? source, int generation = 0) { lock (_gate) { _last = source; _generation = generation; } }
    public bool Accept(PowerAdapterStatus? source, int generation = 0)
    {
        lock (_gate)
        {
            if (generation != _generation || source is null || source == _last) return false;
            _last = source;
            return true;
        }
    }
}
