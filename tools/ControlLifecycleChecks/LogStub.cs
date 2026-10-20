using System;

namespace LenovoLegionToolkit.Lib.Utils;

// No logging or settings initialization in the isolated lifecycle unit fixture.
// The actual message hub and lifecycle implementation are linked unchanged.
internal sealed class Log
{
    public static Log Instance { get; } = new();
    public void Trace(FormattableString message, Exception? ex = null) { }
}
