# Read-only performance procedure

Run from the repository root. Keep hardware commands locked.
Use the production administrator manifest and compatibility check. No fake machine or telemetry files.

## Matched fresh-process diagnostic runs

1. Ask the user to close the game when convenient. Do not terminate it automatically.
2. Finish/close `ProjectLegion.ReadOnlyUiChecks` and `ProjectLegion.ReadOnlyProfiler`.
3. Build/publish Release, run safety assertions, and record hashes of the EXE **and DLLs**. The apphost EXE hash alone cannot identify a source revision.
4. Run `tools/MeasureProductionQa.ps1 -RunsPerMode 3` to a new output directory. It alternates trace-off/on, uses fresh profiles with identical UI/sensor settings, waits ten seconds after initialization, then collects three five-second samples. Each production process exits itself after 45 seconds. It never activates a window.
5. Retain all raw results, including failures/transitions. Exclude runs with game-state changes from a matched quiet group; explain the exclusion and add replacements. Record other active apps, power plan, logical processors, and test time. A missing game process is not proof of a fully idle machine.
6. Report each run and ranges/medians separately for trace-off/on. Stage timestamps are from the application startup stopwatch. `UiInitialized` is not proof of visible/interactive pixels. These are minimized diagnostic runs and must not be compared as improvements over visible operation or a previous unmatched session.

The session-only `--qa-exit-after-seconds=15..600` requires both `--read-only` and a marked isolated QA profile. Only QA profiles have separate instance mutexes and non-activating startup, so they cannot signal the user's existing session. Normal profile behavior is unchanged. `qa-session.json` captures startup/device/first-sample and shutdown milestones even when trace logging is off. File writes for these bounded milestones are shared overhead in both modes.

## Navigation and longer lifetime

Run `ProjectLegion.ReadOnlyUiChecks.exe <fresh-output-directory> --performance-only --no-trace` after production startup samples finish. This separate non-activating transparent test host loads the actual production assemblies. It measures Home with animations attached, temporarily detached, and restored (20 seconds each); About with sensor UI hidden (20 seconds); every page; then Home for another 60 seconds. Telemetry polling intervals are never changed. The animation experiment affects this test host only and restores all detached behaviors.

Inspect five-second CPU/working-set/private-memory/handle/thread series, navigation endpoints, and Home after returning. Repeat before claiming a causal animation cost. Transparent/offscreen rendering is not representative of foreground composition or hardware acceleration. This test does not reproduce the full production startup entry point.

## Managed stack sampling

`ProjectLegion.ReadOnlyProfiler.exe <pid> 20 <new-output-directory>` attaches to only the selected managed process with standard administrator permissions and writes local EventPipe/ETLX data plus inclusive frame counts. Do not bypass a denied diagnostic pipe or a security prompt. If the target exits during capture, label the attempt failed. Inclusive counts include blocked/external samples and are not exclusive CPU percentages. Compare stacks with process CPU and stage experiments; do not optimize a polling interval from frame presence alone. Do not install/update hardware drivers.

## Foreground completion, when the user is free

Repeat fresh starts with the game, harness, and profiler closed. Observe actual visible pixels and verify a harmless sidebar navigation action before assigning a visible/interactive timestamp. Correlate observation timestamps with process creation and device/first-sample milestones. Do not substitute a nonzero handle, `IsWindowVisible`, `UiInitialized`, or an offscreen bitmap for this measurement.

Verify mouse/keyboard navigation, Tab/focus indication, scrolling, minimize/maximize/restore, close/tray semantics, actual monitor DPI transitions and multi-monitor placement. Use a fresh profile, repeat comparable trace-off/on runs, and confirm exit/no residual test processes. Preserve the user's original JSON fingerprints and do not persist hardware actions. Keep outstanding foreground checks explicit until performed.
