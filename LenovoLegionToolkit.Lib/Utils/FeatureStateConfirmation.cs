using System;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>Presentation state only. Requests never become confirmed without a device readback.</summary>
public sealed class FeatureStateConfirmation<T> where T : struct
{
    public T? LastConfirmed { get; private set; }
    public T? Requested { get; private set; }
    public bool IsPending { get; private set; }
    public string? Error { get; private set; }

    public void Observe(T state) => LastConfirmed = state;

    public void Begin(T requested)
    {
        if (IsPending) throw new InvalidOperationException("A setting change is already pending.");
        Requested = requested;
        IsPending = true;
        Error = null;
    }

    public bool Complete(T observed)
    {
        if (!IsPending || Requested is null)
            throw new InvalidOperationException("No setting change is pending.");
        Observe(observed);
        IsPending = false;
        var matches = observed.Equals(Requested.Value);
        Error = matches ? null : "The device did not confirm the requested setting.";
        return matches;
    }

    public void Fail(string message)
    {
        IsPending = false;
        Error = message;
    }
}
