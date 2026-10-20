# Project Legion Phase 2 integration

## Implementation checklist

- [x] Inspect the existing dirty working tree; preserve all earlier source, branding, licensing and resource edits.
- [x] Establish that preview and production share `App.xaml.cs`, `MainWindow`, pages, Autofac registrations and hardware implementations. No parallel preview services or example telemetry were found.
- [x] Retain the approved AMOLED/crimson resources and real sidebar routes.
- [x] Audit eager dependency creation, startup, event listeners, monitoring, automations and shutdown for writes.
- [x] Open production with real navigation and reads, and require a session opt-in before commands.
- [x] Keep startup actions inactive while loading saved automation pipelines for display/editing.
- [x] Reuse feature compatibility checks, original firmware commands and device locks. Add shared serialization and readback to the common performance/battery/graphics/light toggle and combo controls.
- [x] Mark hardware controls unverified; distinguish saved RGB configuration from firmware readback.
- [x] Make preview a separate build option; preserve its existing executable while validating production.
- [ ] Build the complete managed application in Release and record optional packaging diagnostics.
- [ ] Run existing checks and inspect the real production window, navigation, telemetry, persistence and resource disposal.
- [ ] Record measurements, limitations and the next hardware tests that need user approval.

## UI to implementation map

| Component | Existing implementation | Read behavior / command status |
| --- | --- | --- |
| App entry and lifecycle | WPF `App.xaml.cs`, `MainWindow`, Autofac | Production follows compatibility, tray, navigation, settings and disposal. Preview returns through its diagnostic branch only. |
| Palette/sidebar | `Branding.xaml`, `ThemeManager`, `MainWindow.xaml` | Shared by the production and diagnostic targets; no second visual design. |
| Device information | `Compatibility.GetMachineInformationAsync`, MainWindow device indicator | Actual device data; original compatibility gate retained. |
| Home | `DashboardPage`, `DashboardGroupControl`, existing feature controls | Real feature getters and sensor services. No sample values. |
| Performance | `PowerModeFeature`, `ITSModeFeature`, existing mode controls | Capability checks and feature readback; setters unverified on hardware. |
| Thermals | `ISensorsController`, `SensorsControl`, `GodModeSettingsWindow` | Real WMI/NVAPI/Windows readings where available; original custom fan editor, AC and God Mode checks. |
| Battery | `Battery.GetBatteryInformation`, `BatteryFeature`, night-charge control | Actual battery status and firmware modes; unsupported custom limits are not invented. |
| Graphics | `HybridModeControlFactory`, `GPUController`, `GPUOverclockController` | Original getters and compatibility handling; GPU mutations unverified. |
| Keyboard | Spectrum/RGB controllers and white-backlight features | Spectrum/white firmware reads; legacy RGB API returns saved configuration, explicitly labeled. |
| Automation | `AutomationProcessor`, saved `AutomationSettings` | Pipelines load without trigger subscriptions or startup execution. Explicit session enable starts listeners. |
| Settings | Existing SettingsPage and controls | Existing settings architecture. Writes require session opt-in through the real UI. |

## Startup and shutdown audit

Production does not call saved power/ITS restore, battery mode restore, dGPU eject/notify, GPU/AMD overclock apply, RGB ownership/preset restore, Aurora, LampArray initialization, AI, macro hooks, extension entry points or hardware command IPC merely to show the UI. Their implementations are retained. Automatic input listeners wait for the session control opt-in. Observation listeners remain available; their hardware-changing branches are guarded. Eager dGPU keep-awake creation was removed, and a mere monitoring read cannot apply a saved GPU state while controls are locked.

The UI control lock is deliberately session-only, not a saved setting. `--read-only` keeps it locked for validation; navigation and real telemetry remain available. The separate `--ui-preview` branch remains visually inspectable with navigation blocked. A `ProjectLegionPreviewBuild=true` build forces that branch without requiring an argument and uses the asInvoker manifest; production retains its administrator manifest.

Closing a locked production session stops GPU monitoring and automation listeners, releases observation resources through Autofac disposal, and does not restore default thermal/power settings or release RGB ownership it never acquired. Feature control read failures display an unavailable/unsupported state. Sensor and battery polling back off after failures rather than spinning.

## Build entry points

`ProjectLegion.slnf` includes all eight managed projects from the upstream solution. It excludes only the optional LampArray MSIX packaging project, whose DesktopBridge imports require Visual Studio packaging tooling. The original `.sln` and `.wapproj` remain available.

Restore with `dotnet restore ProjectLegion.slnf -p:Platform=x64 --disable-parallel -m:1 -nr:false`. A single node with node reuse disabled resolves the silent restore graph failure observed in the restricted runner. NuGet DNS access requires the ordinary host/network environment.

Build the actual application with `dotnet build ProjectLegion.slnf -c Release -p:Platform=x64 -p:UseSharedCompilation=false --no-restore -m:1 -nr:false`.

Hardware changes have not been approved for validation. Performance, battery conservation/charge limits, lighting, graphics, custom cooling, automation and advanced commands remain explicitly unverified until each supported action is approved and tested with readback.

## Validation results

Pending runtime validation. A successful build alone is not evidence of completed integration.
