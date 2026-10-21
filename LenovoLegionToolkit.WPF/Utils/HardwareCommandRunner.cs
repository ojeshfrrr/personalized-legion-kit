using System;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Utils;

public static class HardwareCommandRunner
{
    public static Task RunAsync(Func<Task> change) => HardwareCommandGate.RunAsync(change);
}
