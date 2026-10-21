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

## Remaining Windows checks

The production manifest requires administrator approval. No production/UAC launch was made during this integration. Native taskbar, Alt+Tab, tray, notifications, actual main-window/About rendering, Windows icon-cache behavior, lighting-helper splash and installer runtime appearance remain unverified. No installer compiler was available on PATH and no installer was installed. The candidate is unsigned and framework-dependent; an installed .NET Desktop Runtime is required. Preserve the existing license/source notices when distributing.
