# Project Legion

Project Legion is an unofficial independent derivative of Lenovo Legion Toolkit. It is not affiliated with or endorsed by Lenovo or the original maintainers.

This fork reuses the existing C# and WPF implementation, including its device detection, hardware interfaces, controls, automation, tray behavior, and configuration model. Compatibility remains governed by the upstream implementation; this fork does not claim support for unverified devices.

## Build

Use the .NET 9 SDK on Windows. Restore and build the WPF project from the repository root:

```powershell
dotnet restore LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -p:Platform=x64
dotnet build LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64 --no-restore -m:1
```

To run the application from source:

```powershell
dotnet run --project LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64
```

`make.bat` also publishes the app and creates an Inno Setup installer; it requires Inno Setup 6. The app targets .NET 9 and its release manifest requests administrator access for hardware controls. UIAccess is disabled because this fork has no owner-provided trusted signing certificate. A build signed with your own trusted certificate can opt into `app.uiaccess.manifest` with `-p:ProjectLegionUiAccess=true`.

The LampArray MSIX project has a Project Legion package identity and signing is disabled by default. The installer does not install or trust a certificate; the Windows Dynamic Lighting extension was not packaged in this baseline and requires a package signed by a certificate you control.

## Project-specific settings

- The update feed is configured in `LenovoLegionToolkit.Lib/ProjectBranding.cs` for this repository. Open **Updater** in the sidebar to check versions, choose **Preview + stable** or **Stable**, view the last result, and download an available update. New profiles default to Preview; existing preferences are preserved. Updating software does not require enabling laptop hardware controls.
- Checking for updates shows newer versions and release notes. **Download update ZIP** verifies the Windows x64 asset's size and SHA-256 against GitHub metadata. **Install and restart** requires explicit confirmation, stages a separate protected installation, retains a verified previous-version backup and preserves `%LOCALAPPDATA%\ProjectLegion`. Updater offers rollback when that backup is available. Uncertain startup remains pending rather than being force-closed. Manual ZIP extraction remains available. ZIPs are unsigned; a checksum does not establish a trusted publisher. Read-only/preview sessions cannot install, restart, download or mutate preferences. Self-contained installations select self-contained updates instead of silently introducing a Desktop Runtime prerequisite.
- Edit the charcoal shell background in `LenovoLegionToolkit.WPF/Branding.xaml` and the default ice-blue accent in `LenovoLegionToolkit.WPF/Utils/ThemeManager.cs`.
- Replace the generated image assets with your own when ready. The app's data is stored under `%LOCALAPPDATA%\ProjectLegion`; on first launch, top-level JSON settings are copied from the upstream folder without overwriting or deleting the original data.

## Live telemetry while gaming

Home and Thermals refresh visible sensor readings every **1 second** while Project Legion is the active foreground window, including when Valorant is running. Switching away, minimizing, hiding the page or closing the window stops these optional reads. Returning to the foreground requests a fresh reading; an already-running native query may finish, but its retired result is not displayed.

This foreground exception applies to sensor readings. Optional GPU status/process enumeration retains its gaming pause, Mouse settings are never periodically polled, and explicitly enabled OSD, HWiNFO, hardware-control and automation features keep their existing policies. Refreshing readings does not write hardware settings. No FPS improvement or FPS-neutrality claim has been established by controlled measurements.

## License and attribution

Release-candidate procedures, signing prerequisites, matched gaming captures and pending Windows checks are documented in [RELEASE-READINESS.md](RELEASE-READINESS.md). These procedures are not evidence that a live update, clean-account test or FPS benchmark has passed.

The checked-out upstream revision is licensed under GPL-3.0. This derivative is distributed under the same license; see [LICENSE](LICENSE) and [NOTICE](NOTICE). The original work is by Bartosz Cichecki, Kaguya, and Dr. Skinner. Third-party copyright and license notices are retained in the source.

Original source: [Lenovo Legion Toolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit).
