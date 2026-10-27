namespace LenovoLegionToolkit.Lib.Utils;

public enum OptionalUiState { Active, Hidden, Minimized, Inactive, GamingPaused, CheckingGame }

public static class OptionalUiPolicy
{
    public static OptionalUiState Evaluate(bool loaded, bool visible, bool hasWindow, bool foreground,
        bool minimized, bool requireForeground, bool suspendForGaming, bool workPaused, bool gameRunning)
    {
        if (!loaded || !visible || !hasWindow) return OptionalUiState.Hidden;
        if (minimized) return OptionalUiState.Minimized;
        if (requireForeground && !foreground) return OptionalUiState.Inactive;
        if (suspendForGaming && workPaused) return gameRunning ? OptionalUiState.GamingPaused : OptionalUiState.CheckingGame;
        return OptionalUiState.Active;
    }

    public static string Describe(OptionalUiState state) => state switch
    {
        OptionalUiState.Active => "Initializing readings…",
        OptionalUiState.Minimized => "Monitoring paused — window minimized.",
        OptionalUiState.Inactive => "Monitoring paused — bring Project Legion to the foreground.",
        OptionalUiState.GamingPaused => "Monitoring paused while Valorant is running.",
        OptionalUiState.CheckingGame => "Monitoring paused — checking game presence.",
        _ => "Monitoring paused — page is not visible."
    };
}
