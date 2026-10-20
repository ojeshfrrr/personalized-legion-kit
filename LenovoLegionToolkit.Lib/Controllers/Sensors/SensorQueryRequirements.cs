using System;
using System.Collections.Generic;

namespace LenovoLegionToolkit.Lib.Controllers.Sensors;

[Flags]
public enum SupplementalSensorQueries { None = 0, Lenovo = 1, Battery = 2 }

// The advanced snapshot already supplies CPU/GPU/memory/storage data.
// Query the other providers only for values the selected view actually consumes.
public static class SensorQueryRequirements
{
    public static SupplementalSensorQueries GetSupplementalQueries(IReadOnlySet<SensorItem> items)
    {
        var scope = SupplementalSensorQueries.None;
        if (items.Contains(SensorItem.CpuFanSpeed) || items.Contains(SensorItem.GpuFanSpeed) ||
            items.Contains(SensorItem.PchTemperature) || items.Contains(SensorItem.PchFanSpeed))
            scope |= SupplementalSensorQueries.Lenovo;
        if (items.Contains(SensorItem.BatteryState) || items.Contains(SensorItem.BatteryLevel))
            scope |= SupplementalSensorQueries.Battery;
        return scope;
    }
}
