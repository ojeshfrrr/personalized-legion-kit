# Project Legion — split-blade identity

Original geometric artwork and outlined lettering. No third-party fonts, scans, textures or logo files are embedded. See the repository LICENSE (GPL-3.0); existing upstream and third-party notices remain intact. Project Legion is independent and does not imply Lenovo or Razer affiliation.

## Deliverables

- `project-legion.svg`: standalone, editable vector emblem. No bitmap or SVG filters.
- `wordmark.svg` / `wordmark.png`: original outlined lockup, with transparent 900 × 240 PNG.
- `project-legion.ico`: ten Windows sizes — 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px, 32-bit alpha.
- `sidebar.svg`, `sidebar-32.png`, `sidebar-64.png`, `sidebar-wordmark.svg` / `.png`: compact variants.
- `titlebar.svg`, `titlebar-16.png`, `titlebar-32.png`: simplified native-title variants.
- `monochrome.svg`: one-color `currentColor` version; `monochrome-black.svg` for light/print contexts and `monochrome.png` for dark surfaces. `wordmark-monochrome.svg` is also color-inheriting.
- `icons/`: individually rendered icon frames. Windows icons use a black tile; the other PNGs are transparent.

## Design and usage

Pure black `#000000`, gunmetal `#59616D`, steel `#A6AFBD`, crimson `#FF334F`, near-white `#ECEFF4`. Full-size artwork uses a few planar facets; 16–32 px icons remove the bevels and use two solid blades. No glow, blur, small lettering or animated effects in icons. Maintain at least the supplied clear space. Use the emblem alone at small sizes; do not shrink the entire wordmark into a taskbar icon.

`geometry.json` is the canonical editable path/palette source, including original glyph outlines. Run `python tools/generate_brand_assets.py` from the repository root (or the existing PowerShell wrapper). Windows WPF renders static PNGs; no new runtime package or image renderer is required by the application. SVGs and frozen native DrawingImage resources share these same paths.

## Integration

The WPF executable and tray resource use `Assets/ProjectLegion.ico`. BaseWindow uses the same multi-size icon for native windows/taskbar/Alt+Tab. The main title bar and sidebar use small, frozen vector resources in `Assets/Branding/Identity.xaml`; existing row/column sizes and compact-mode behavior are preserved. New builds/publishes include a portable `Branding/` folder. Installer artwork and LampArray helper icons are generated from the same master. Existing Release candidate ZIPs are not modified by asset generation.

About uses the full frozen vector wordmark. Notification content keeps its meaningful event symbols; the native application identity comes from the executable icon. Third-party credits remain textual attribution, not replacement brand marks.

The installer small-header artwork is a square compact emblem, avoiding distortion of the wide wordmark in Inno Setup’s small-image slot. Run `python tools/verify_branding.py <build-or-publish-directory>` to verify all ten native executable icon payloads and copied kit files. `tools/verify_branding_vectors.ps1` renders frozen vectors independently of application startup.

The WPF sidebar, title-bar emblem, About wordmark and monochrome fallback use embedded transparent PNG renditions. The original SVGs remain editable source artwork; Windows executable/taskbar/tray icons retain the multi-size ICO. PNGs are loaded once and frozen, with no runtime vector renderer.
