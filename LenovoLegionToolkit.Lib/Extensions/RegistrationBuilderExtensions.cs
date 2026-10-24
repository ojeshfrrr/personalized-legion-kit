using System;
using Autofac;
using Autofac.Builder;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Extensions;

public static class RegistrationBuilderExtensions
{
    public static void AutoActivateListener<T>(this IRegistrationBuilder<IListener<T>, ConcreteReflectionActivatorData, SingleRegistrationStyle> registration) where T : EventArgs
    {
        registration.OnActivating(e =>
        {
            // Diagnostic sessions poll explicit getters only. No hardware listener is started.
            if (AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview)
                return;
            // Listeners may query hardware on OS notifications. Start only after explicit session opt-in.
            if (!HardwareRuntime.ControlsEnabled)
                return;
            e.Instance.StartAsync().AsValueTask();
        }).AutoActivate();
    }
}
