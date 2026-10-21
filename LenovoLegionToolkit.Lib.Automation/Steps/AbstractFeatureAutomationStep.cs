using System;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Automation.Steps;

public abstract class AbstractFeatureAutomationStep<T> : IAutomationStep<T> where T : struct
{
    private readonly IFeature<T> _feature;
    public T State { get; }
    protected AbstractFeatureAutomationStep(T state) : this(state, IoCContainer.Resolve<IFeature<T>>()) { }
    protected AbstractFeatureAutomationStep(T state, IFeature<T> feature) { State = state; _feature = feature; }
    public Task<bool> IsSupportedAsync() => _feature.IsSupportedAsync();
    public virtual async Task RunAsync(AutomationContext context, AutomationEnvironment environment, CancellationToken token)
    {
        await HardwareCommandGate.RunAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (!await _feature.IsSupportedAsync().ConfigureAwait(false) ||
                Array.IndexOf(await _feature.GetAllStatesAsync().ConfigureAwait(false), State) < 0)
                throw new NotSupportedException("The requested automation profile is not supported.");
            token.ThrowIfCancellationRequested();
            var observed = await _feature.GetStateAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!State.Equals(observed))
            {
                await _feature.SetStateAsync(State).ConfigureAwait(false);
                // Even if cancelled during the write, drain it and read back; never retry a write.
                observed = await _feature.GetStateAsync().ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!State.Equals(observed))
                throw new InvalidOperationException("Automation profile change was not confirmed by device readback.");
            MessagingCenter.Publish(new FeatureStateMessage<T>(observed));
        }, token).ConfigureAwait(false);
    }
    public Task<T[]> GetAllStatesAsync() => _feature.GetAllStatesAsync();
    public abstract IAutomationStep DeepCopy();
}
