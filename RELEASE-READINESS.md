# Release-readiness validation

## Updates and rollback

Only an explicit **Install and restart** confirmation may install or restart. The downloaded ZIP is rehashed while held open against modification, safely extracted into a fresh administrator-protected `Program Files\ProjectLegionUpdates` version directory and checked against the release version. Configuration JSON, private keys and existing profile data are never imported from the package or copied into the backup. The previous application's binaries are retained separately. The old installation is not overwritten or deleted.

The hardware-free UpdateHost inherits the application's existing privileges; it cannot elevate itself. It verifies the requesting PID/start time, package file manifest and backup before acknowledging the handoff. The current app remains open if the helper fails to acknowledge. A single helper owns restart; normal shutdown releases the old app first. Only an acknowledged successful startup redirects existing matching shortcuts. Early process exit triggers a verified previous-version restart. Uncertain/time-limited startup stays pending with the previous version retained; no process is killed. Rollback in Updater is explicit and warns about settings compatibility. Existing user configuration is retained, not downgraded or guessed.

Offline tests do not prove Windows ACL behavior, UAC cancellation, process handoff or rollback on the physical machine. Those require an explicitly approved, supervised update session. Until that passes, treat this as a release candidate and retain the manual ZIP workflow.

## Controlled gameplay comparison

Reuse an already available PresentMon/CapFrameX capture tool; do not install a tool, launch Valorant, manipulate Vanguard or change game/hardware settings automatically. Tool/version, machine, game version, resolution, graphics settings, FPS caps, power/fan/GPU mode, map/scenario, capture duration, background applications and warm-up must match across runs.

The user performs at least three 60-second runs with Project Legion fully exited and three with it running normally while **Valorant stays foregrounded**. Alternate conditions and use the same repeatable route/scenario. Optionally repeat with Project Legion minimized. Foregrounding Project Legion changes game focus and can invoke game background FPS limits: treat that as a separate diagnostic, not an equivalent FPS comparison. Do not change controls or apply profiles between conditions.

Export frame times in milliseconds. `tools/analyze_gaming_captures.py manifest.json --output new-results.json` reports per-run FPS, 1% lows (reciprocal mean of the slowest 1% of frame intervals), P95/P99 frame times, condition medians and ranges. It rejects invalid samples and insufficient replication. Results cannot establish causation or FPS neutrality by themselves.

Manifest example (repeat each condition with three independent files):
```json
{"captureTool":"PresentMon/CapFrameX: record exact version", "conditions":"Record the matched setup above", "runs":[
  {"condition":"closed","file":"closed-1.csv","column":"MsBetweenPresents","application":"VALORANT-Win64-Shipping.exe"},
  {"condition":"running","file":"running-1.csv","column":"MsBetweenPresents","application":"VALORANT-Win64-Shipping.exe"}
]}
```
The sample is intentionally incomplete and is not benchmark evidence. Column names must match the capture export; no units or renamed columns are guessed. [PresentMon source](https://github.com/GameTechDev/PresentMon) documents the capture implementation.

Reuse `tools/MeasureProductionQa.ps1` for isolated process CPU/memory/handle/thread measurements when explicitly approved. Its startup milestones are not proof of visible/interactable UI. Trace/no-trace results must remain separate.

## Standalone and signing

`python tools/package_release.py --self-contained` uses a separate source/build tree and the existing package cache. It bundles the Windows x64 Desktop Runtime for both application and update helper, checks runtime configuration and required runtime files, retains LICENSE/NOTICE and includes matching source. The larger ZIP avoids requiring an installed Desktop Runtime, but must be republished for bundled-runtime security updates. No single-file trimming or dependency upgrades are introduced.

Builds remain **unsigned** until a trusted signing identity is supplied locally. Do not upload private keys/passwords or create a self-signed trust root. `tools/sign_release.ps1 -WhatIf` previews signing of owned binaries only, using an existing certificate-store key and HTTPS timestamp service. Signing is a separate new staging step before ZIP creation; regenerate manifests/hashes afterward. Third-party signatures/licenses remain intact. [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) describes signing/verification and timestamp options. No certificate purchase, certificate installation or trust changes are authorized by this preparation.

## Focused Windows checks

Use the exact candidate and a fresh marked read-only profile after explicit UAC approval. Record executable path, PID, hashes and scale/monitor configuration. Check Home, Thermals, Battery, Mouse, Updater, keyboard Tab/Shift+Tab/Enter/Escape, visible focus, vertical scrolling, 760×640 and 1250×780, native minimize/restore/close-to-tray, 100/125/150/200% display scaling and multiple-monitor placement where equipment exists. Keep hardware controls locked. Do not alter the real user's display settings merely to mark these checks complete.

On a separate clean Windows account/VM, test extraction, first startup, framework prerequisite handling, self-contained startup without an installed runtime, explicitly supervised install/restart/rollback, and removal of application files while preserving user settings. Creating an account/VM, elevating an installer, accepting Windows prompts or deleting installations requires its own authorized session. No clean-account or live update result is implied by an offline build.
