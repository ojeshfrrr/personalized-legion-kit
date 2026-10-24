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


## Earlier on-demand policy — superseded below (2026-10-21)

The current implementation keeps the approved design and uses timestamped snapshots plus explicit Refresh. Earlier polling-based timings above are historical procedures, not comparable evidence of an improvement. A minimized process is now expected to have no UI sensor samples until its page is shown.

| Work | Previous source behavior | Current policy |
|---|---|---|
| Standard sensor UI | 0.5–5-second refresh loop; starts GPU worker | One page-open read and explicit Refresh; no worker lease |
| Advanced sensor UI | Recurring shared sensor subscription plus supplemental queries | One scoped snapshot of an already initialized provider; never initializes a driver to fill a page |
| Battery | Two-second loop | One page-open read and explicit Refresh |
| GPU control/management | Optional monitoring leases and repeated status/app enumeration | One snapshot and explicit Refresh; app-list work is cancelled/drained on hide |
| HWiNFO | One-second integration resumed from saved preference | Explicit session toggle only; serialized worker ownership, actual running status, no cleanup of unowned registry state |
| Live OSD | Could reopen from saved preference at startup or shell recreation | Explicit session activation; recreation only restores an already visible overlay |
| Saved P-state | Could resume enforcement on worker initialization/getter | Saved lock is inactive; only an explicit successful request enables session enforcement |
| Mouse | Already on demand | Same verified protocols; minimize/game/page-hide now retires reads; bank rows rebuilt only on changed bank data |

`DemandReadSession<T>` coalesces Refresh, cancels retired lifetimes, drains non-cancellable native calls before replacement, and suppresses late results/errors. Advanced UI and Macro subscriptions detach when unloaded. Existing explicitly enabled automation, P-state enforcement, OSD, HWiNFO, hotkeys and power-overlay tracking remain available. These opted-in consumers may still need background reads: GPU monitoring uses its configured interval, OSD its selected interval, HWiNFO one second, and the existing power-overlay watcher 500 ms when enabled. The five-second game-presence check is process metadata, not a hardware sensor query. This is not a claim of zero total CPU usage.

Validation: Library and WPF Release build successfully with zero warnings/errors. 344 safety/mouse/targeted assertions pass; lifecycle checks report 25 active / 0 inactive callbacks over 25 cycles, one read for 50 concurrent requests, and one callback after duplicate Load. Four detached Mouse layouts and 32 scaled hit-region checks pass. Eight additional detached snapshot layouts check Refresh width and the Thermals cooling card at 1250×780 and 760×640. These render full scroll content with no device constructors; Battery hardware controls are omitted. They do not establish foreground usability, runtime theme appearance, or physical telemetry accuracy.

Evidence: `BuildProjectLegion/DemandReads/` contains build logs, assertions, lifecycle JSON, detached images/layout results and `query-counts.json`. In a deterministic five-tick fake-provider replay, the former schedule issues six reads versus one initial read for the demand session; hidden additional reads are zero and peak concurrent reads is one. This is a scheduling test, not a measurement of application CPU, memory or Valorant FPS.

No production application, elevated process or physical hardware operation was launched. Test settings live only in newly generated isolated assertion profiles. No real user configuration or existing QA profile was edited. Live working set, private bytes, managed heap, peak memory, startup/navigation/minimize resource series and shutdown measurements remain unmeasured in this pass. The 20 MB stretch target and any gap to it cannot be established from this evidence; no memory/FPS reduction is claimed. Native calls that do not accept cancellation may finish after hide, but their retired results are discarded and a replacement waits for completion.


## Current foreground telemetry policy — 2026-10-21

- Home and Thermals: 1,000 ms; Battery: 5,000 ms. One timer per active telemetry owner, implemented inside the existing demand-read session. CPU utilization prepares its baseline once per activation, not on every tick. No global 500 ms timer.
- Unload, hide, minimize, focus loss and detected Valorant suspend optional telemetry. Resuming takes a fresh initial reading. Retired native calls drain, late samples are discarded, and requests coalesce. Explicitly enabled OSD/HWiNFO/background controls retain their existing intervals and ownership.
- Graphics: entry/reactivation and relevant operation readbacks; no periodic app enumeration. Its management dialog retains one useful explicit list refresh. Removed the new per-card/page Refresh buttons from sensor, Battery and GPU widgets; sensor context-menu recovery remains unobtrusive.
- Mouse: DPI/polling plus bank/assignment reads on entry, verified readbacks after explicit Apply/Restore, no timer. Focus changes or game-presence changes do not cancel explicit mouse operations. Navigation/minimize still retire its page session. Drafts, sliders and button selection do not query or write the device.
- Static pages have no new recurring hardware reads. Configuration guards, verified protocols and the compact Thermals layout remain intact.

Validation: Library and WPF Release succeed with zero warnings/errors. 370 safety/mouse/targeted assertions pass, including controlled ticks, actual PeriodicTimer cancellation, duplicate activation/refresh, stale samples, navigation/minimize retirement, immediate resume, read-only routing and getter-only callbacks. Existing lifecycle checks pass (25 active/0 inactive callbacks, one read for 50 concurrent requests). Four detached Mouse layouts, 32 vector hit checks and eight detached telemetry layouts pass at both target sizes; no redundant telemetry Refresh buttons remain. These are offline checks, not a foreground runtime session or a gaming benchmark.

Evidence: `BuildProjectLegion/ForegroundTelemetry/`. Earlier six-versus-one fake query counts test one-shot behavior, which remains supported for nonperiodic consumers; they do not describe the current live Home/Thermals cadence. No production launch or physical hardware changes were performed in this implementation pass. No FPS or memory improvement is claimed.
