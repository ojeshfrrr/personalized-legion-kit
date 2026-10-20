using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

/// <summary>On-demand operations; creating a service never opens the mouse.</summary>
public interface IMouseControlService
{
    Task<MouseDpi> ReadActiveDpiAsync(CancellationToken token = default);
    Task<MousePollingRate> ReadPollingRateAsync(CancellationToken token = default);
    Task<MouseDpiApplyResult> ApplyActiveDpiAsync(int x, int y, CancellationToken token = default);
    Task<MousePollingApplyResult> ApplyPollingRateAsync(MousePollingRate rate, CancellationToken token = default);
}
