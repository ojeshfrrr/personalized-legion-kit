# Branding integration verification

The existing kit in `assets/branding` supplies executable/window/tray ICOs, native frozen sidebar/title-bar/About vectors, square installer header artwork, and scale-qualified lighting-helper images. The generator uses the same canonical paths throughout. Functional notification symbols and upstream/third-party credits are retained. No additional application splash screen or Credits page exists.

Release builds and publishes copy the portable kit to `Branding/`. The packaging script requires the key deliverables and includes LICENSE, NOTICE and a corresponding source archive in a new, uniquely named candidate; existing candidate ZIPs remain untouched.

## Executed checks (2026-10-21)

- WPF Release build: zero warnings/errors.
- 370 existing safety/mouse/lifecycle assertions; lifecycle probe: 25 active callbacks, zero inactive callbacks, 50 concurrent requests coalesced to one read, one callback on duplicate load.
- Four detached Mouse layouts and eight detached telemetry layouts at the existing target dimensions, plus 32 scaled hit-region checks.
- Six native frozen identity resources rendered independently at small/sidebar/About sizes (16 renders); full wordmark and small icons inspected as images.
- `verify_branding.py` compares all ten PE icon payloads byte-for-byte against the kit, verifies installer/WPF ICO equality, and checks all 28 copied Branding files by SHA-256.
- Package generation verifies every published file against its build input, checks declared dependencies, verifies archive payload hashes, and includes matching working-tree source. The source snapshot includes preserved, uncommitted performance work; the branding commit selectively excludes that work.

These checks use detached resources and offline providers. They do not launch the production application, access hardware, or alter user profiles. Detailed generated evidence remains under ignored `BuildProjectLegion/Branding/`.

## Windows checks at the original integration checkpoint

The production manifest requires administrator approval. No production/UAC launch was made during this integration. Native taskbar, Alt+Tab, tray, notifications, actual main-window/About rendering, Windows icon-cache behavior, lighting-helper splash and installer runtime appearance remain unverified. No installer compiler was available on PATH and no installer was installed. The candidate is unsigned and framework-dependent; an installed .NET Desktop Runtime is required. Preserve the existing license/source notices when distributing.

## Controlled smoke-test update (2026-10-21)

**User-observed outcome:** The user reports that the controlled Release session ran with UAC approval, Valorant closed and an isolated `--read-only` profile. Application UI, branding, navigation and tray worked. CPU/GPU readings eventually appeared. These are direct user observations; the agent did not launch or interact with the application during this evidence update.

**Independent runtime evidence:** A snapshot of the latest isolated-profile `qa-session.json` records PID 22080, `Trace: false`, startup completion at 1,940.4529 ms and `FirstSensorSample` at 55,331.6929 ms after the recorded startup stopwatch began. The sensor-publication milestone is about 53.39 seconds after startup completion. It proves that the sensor publication callback was reached; the record does not contain individual CPU/GPU values. The available log directory is empty. Missing trace output is not evidence of an exception. The snapshot contains no shutdown milestone, so it establishes at least 55.33 seconds of activity, not a complete session duration.

**Sensor delay:** Recorded as an observation, not a confirmed defect. Foreground/game-gate transitions and individual provider-stage durations were not logged, so no cause is assigned. No sensor logic, polling, settings, privilege model or safeguards changed.

**Evidence boundaries:** The earlier screenshot independently showed Home, title-bar/sidebar branding, the visible read-only banner and a taskbar icon, while its sensor values were still dashes. The later user confirmation supersedes the earlier unresolved outcome for eventual readings, navigation and tray. The older failed non-elevated launch (error 740) and short PID 27556 session remain historical records. Candidate files were verified on disk, but the runtime session does not record the actual image path or assembly hashes; exact running-build attribution is still unverified. About, installer/helper/notification surfaces and quantitative performance were not separately established by this session.

Detailed original records and before-update copies remain in ignored `BuildProjectLegion/Branding/post-commit-smoke-076dec4f/`; `successful-session-update/qa-session-observed.json` preserves the inspected runtime snapshot. No machine-specific profiles or diagnostic artifacts are committed. Existing Release ZIPs and working source remain unchanged. This checkpoint updates documentation only; no rebuild or new test run was performed.
