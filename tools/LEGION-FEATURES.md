# Focused Legion capability and confirmation pass

## Compact source audit

| Area | Existing implementation and API | Classification / decision |
| --- | --- | --- |
| Battery modes | `BatteryFeature` uses EnergyDrv `IOCTL_ENERGY_BATTERY_CHARGE_MODE`; bit-backed getter distinguishes Normal, RapidCharge and Conservation. `IsSupportedAsync` probes the existing driver getter; BatteryModeControl is already reused by Home/Battery. | Implemented; improve requested/confirmed/error presentation. Numeric charge thresholds/custom limits are not exposed by this API and are not implemented. This does not establish that the physical model lacks such firmware capability. |
| Performance | `PowerModeFeature` uses Lenovo WMI smart-fan mode, model-specific Extreme/GodMode flags, AC constraints and existing transition workarounds; `PowerModeControl` supplies existing tiles/selector. | Implemented; correct summary/settings availability to use readback, not an in-flight request. No universal wattages or thermal limits added. |
| Thermals | ThermalsPage uses safe read-only SensorsControl and model/AC-gated GodMode settings for supported fan curves/power limits. | Implemented; hardware writes unverified in this task. Compact-layout fix retained. No new controls needed. |
| Graphics | HybridModeControlFactory selects BIOS/IGPU-capable/toggle implementation, with existing DGPUNotify switching information and device-specific feature paths. | Implemented; no competing mode selector or polling added. Actual support depends on existing live capability checks. |
| Automation | AutomationProcessor uses explicit session enablement, HardwareRuntime permission, event listeners, serialized pipeline execution, cancellation and shutdown. | Implemented and opt-in; no new rules/background workers warranted by this source audit. No configured automation was enabled or changed. |

The user-observed machine is a Legion 5 Pro 16IAH7H. No hardware was queried or changed for this audit; source compatibility checks remain authoritative at runtime. Existing UI/read-only smoke evidence does not prove physical mode switching.

## Implemented changes

- `FeatureStateConfirmation<T>` is presentation state only: a request cannot become confirmed without observed device readback. Pending requests cannot overwrite one another; mismatches and unavailable readbacks retain separately labelled last-confirmed state.
- The existing combo-box feature path checks session permission and supported states before the existing serialized HardwareCommandRunner. It still invokes the original feature setter followed by a fresh getter. Failed/mismatched writes report an error and refresh the actual selection. Refresh/population and pending-selection callbacks do not initiate writes. No retry/rollback writes are introduced.
- Battery mode text states the device-reported mode, pending request or unverified change, and explicitly says this API does not report numeric charge thresholds. No percentage is fabricated.
- Performance heading and configuration-button eligibility use confirmed readback. Requested tiles cannot relabel the confirmed mode while the operation is pending or has failed.

## Verification boundaries

Focused offline state tests cover pending/confirmed separation, duplicate requests, mismatch, unavailable readback, reconciliation, failure visibility and retry intent. Static integration checks verify supported-state rejection before transmission, central permission/serialization, setter-free refresh and confirmed-state UI binding. The existing safety/mouse/lifecycle suites are also run. These are code/offline checks, not physical hardware validation or a new elevated visual test. No manifest, hardware protocol, user settings, sensor cadence, automation or branding assets changed. Existing Release ZIPs are preserved.

Validation result: Library and WPF Release build succeeded with zero warnings/errors; 389 assertions passed (19 new confirmation/guard checks). Existing lifecycle probe: 25 active callbacks, zero inactive callbacks, one actual read for 50 requests, one callback on duplicate load. Four detached Mouse layouts, eight detached telemetry layouts and 32 hit-region checks passed. The focused state tests use synthetic observed values; the command-lock test executes the real HardwareCommandRunner with a non-hardware delegate and verifies it is never invoked while locked. There was no elevated launch or physical hardware change.
