using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using LenovoLegionToolkit.Lib.Utils;

internal static class ReleaseReadinessChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        OptionalUiState State(bool active = true, bool minimized = false, bool visible = true, bool loaded = true, bool pauseForGaming = false, bool paused = true, bool game = true) =>
            OptionalUiPolicy.Evaluate(loaded, visible, true, active, minimized, true, pauseForGaming, paused, game);
        check(State() == OptionalUiState.Active, "foreground sensor policy permits gaming-time readings");
        check(State(active: false) == OptionalUiState.Inactive && State(minimized: true) == OptionalUiState.Minimized,
            "monitoring explains foreground loss and minimization distinctly");
        check(State(loaded: false) == OptionalUiState.Hidden && State(visible: false) == OptionalUiState.Hidden,
            "unloaded and hidden pages remain stopped");
        check(State(pauseForGaming: true) == OptionalUiState.GamingPaused && State(pauseForGaming: true, game: false) == OptionalUiState.CheckingGame,
            "game-blocked work distinguishes game presence from unavailable detection");
        check(Enum.GetValues<OptionalUiState>().All(state => OptionalUiPolicy.Describe(state).Length > 10), "each monitoring state has a visible explanation");
        var started = 0;
        var reads = new DemandReadSession<int>(_ => Task.FromResult(4), (_, _) => { }, _ => { }, readingStarted: () => started++);
        await reads.RefreshAsync(); check(started == 0, "inactive refresh does not claim initialization started");
        await reads.ActivateAsync(); await reads.DeactivateAsync(); check(started == 1, "active getter reports its actual start once");

    }
}
