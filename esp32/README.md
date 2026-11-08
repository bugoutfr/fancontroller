# ESP32 fan controller firmware

Arduino / PlatformIO firmware for a classic ESP32 (`esp32dev`). The Windows client is in [../windows](../windows/README.md). Fan control runs locally without a phone, computer, or Bluetooth connection.

## Code structure

| Component | Responsibility | Source |
|---|---|---|
| `ThermalController` | NTC acquisition, control, PWM, startup boost, TACH interrupts, and RPM | [ThermalController.cpp](src/ThermalController.cpp) |
| `ConfigurationStore` | Active configuration, dirty state, NVS load/save, and storage errors | [ConfigurationStore.cpp](src/ConfigurationStore.cpp) |
| `CommandProcessor` | Command validation/execution, JSON responses, and status assembly | [CommandProcessor.cpp](src/CommandProcessor.cpp) |
| `BleTransport` | GATT, connection/notifications, receive queue, framing, and transport timeouts | [BleTransport.cpp](src/BleTransport.cpp) |
| `PairingSecurity` | Physical BOOT authorization, single owner, and persistent BLE keys | [PairingSecurity.cpp](src/PairingSecurity.cpp) |

`main.cpp` wires these components together. BLE callbacks enqueue packets; commands and hardware changes are handled in the main loop. The thermal controller exposes typed status snapshots; JSON serialization stays in `CommandProcessor`. Mutable state belongs to component instances. The BLE v1 protocol and NVS format are stable.

- [HardwareConfig.h](include/HardwareConfig.h): pins, PWM polarity, pulses per revolution, and control periods.
- [Controller.h](include/Controller.h): domain structures and pure calculations.
- [ConfigJson.h](include/ConfigJson.h): JSON conversion and validation.

## Wiring

See the [wiring diagram PNG](docs/schema-electrique.png) or [SVG](docs/schema-electrique.svg). Verify fan pinout and voltages before powering the board. Do not connect powered USB and 5 V Molex to VIN together unless supply isolation is confirmed.

| Function | Fan 1 | Fan 2 | Fan 3 | Fan 4 |
|---|---:|---:|---:|---:|
| PWM GPIO | 15 | 16 | 17 | 18 |
| TACH GPIO | 13 | 34 | 14 | 27 |
| Associated sensor | 1 | 1 | 2 | 2 |

- Sensors: GPIO33 and GPIO32 (ADC1).
- Each divider: **3.3 V — fixed 10 kΩ resistor — ADC — 10 kΩ B3950 NTC — GND**.
- TACH inputs use an external **5.2 kΩ pull-up to 3.3 V** and falling-edge interrupts. Never apply 5 V to an ESP32 input.
- TACH filtering: 100 pF from each TACH GPIO to GND. NTC filtering: 100 nF from ADC to GND.
- PWM is 25 kHz / 10-bit. Use a suitable 3.3 V to 5 V level converter for four-wire fans. Share grounds; do not power fan motors from GPIO or the 3.3 V regulator.
- RPM assumes **2 pulses per revolution**; adjust `PulsesPerRevolution` for the actual fans.

## Control behavior

- Two independent profiles, each with 2–8 points. Temperatures must increase strictly from −20 to 120 °C; PWM duty must be non-decreasing from 0 to 100%.
- Linear interpolation is used between points. Outside the profile range, the nearest endpoint duty is used.
- NTC conversion uses the beta equation (10 kΩ, B3950). Samples are averaged from 16 calibrated ADC millivolt readings every 250 ms. The calculation assumes a 3,300 mV divider supply.
- `TemperatureOffsetC` in [HardwareConfig.h](include/HardwareConfig.h) applies a common −0.7 °C calibration correction. Validate calibration across the temperature range; this single-point adjustment does not guarantee full-range accuracy.
- Each fan is in `auto` or `fixed` mode. Fixed mode sets PWM duty, not a target RPM.
- Since firmware **1.4.7**, automatic PWM follows an exponential filter with a 1 s rising
  and 3 s falling time constant, using actual elapsed control time. The state starts from
  the previous requested duty (100% at boot) and follows profile edits without a reset.
  Temperature and RPM telemetry are not additionally smoothed. `target_duty` reports the
  unsmoothed target after start hysteresis; `applied_duty` and diagnostic `pwm_pct` report
  the actual filtered output, including boost/safety overrides.
- Automatic profiles with an initial zero-duty plateau start a stopped fan only at least
  **0.5 °C above the last zero-duty point**. A running fan continues below that start
  threshold until the interpolated target reaches zero, when PWM stops immediately.
  All-zero profiles remain stopped; profiles whose first duty is positive have no start
  hysteresis. Fixed mode bypasses both mechanisms and re-entering automatic mode evaluates
  the start threshold anew.
- Startup boost runs at 100% for about one second, including when moving from 0% to a positive target.
- **Safety override:** a sensor fault or temperature at/above **80 °C** immediately sets both associated fans to 100%, including fixed-mode fans, without smoothing or hysteresis. The requested mode is retained and resumes when the fault clears; automatic PWM falls from 100% through the filter, or stops immediately if its target is zero or below its restart threshold.
- Electrical sensor faults: ≤50 mV is `short_circuit`; ≥3,050 mV is `open_circuit`; calculated temperatures outside −20…120 °C are `out_of_range`. Faulted sensors report a null temperature.
- There is no RPM closed-loop control or automatic recovery when TACH reports zero. Hardware faults, power loss, or MCU reset can still interrupt cooling; use independent thermal protection where required.

## TACH diagnostics

Firmware **1.4.5** fixes a regression in 1.4.4: UART TX storage was still limited to
20 bytes while notification chunks used the negotiated MTU. TX capacity and chunk limits
now share a 512-byte constant, and the stored bytes are checked before notification.
Version 1.4.4 can time out on initial configuration reads; this is not a pairing failure.

Since firmware **1.4.1**, diagnostics are available over the existing encrypted BLE connection, without USB. In the
updated Windows app's Settings, select **Capture 30 s** or **Capture 60 s**, then **Export CSV**.
Install this firmware using the existing OTA workflow if USB power isolation is unknown.
Do not connect powered USB and VIN simultaneously unless the board's isolation is confirmed.

Firmware 1.4.4 retains the last 128 complete snapshots in RAM, approximately one per second
(earlier diagnostic versions retained eight). UART notifications use the negotiated ATT MTU
minus three bytes, capped at 512 bytes, with the existing 20 ms pacing. This prevents enlarged
diagnostic responses from outrunning the history buffer on typical negotiated MTUs; the
larger history also accommodates slower minimum-MTU transfers for a 60-second capture.
`get_status` advertises `tach_diagnostics_supported: true`. Send
`{"cmd":"get_tach_diagnostics"}` to obtain `data.latest_sequence` without historical data.
Then send `{"cmd":"get_tach_diagnostics","after":N}` to read the next retained window.
The response contains `latest_sequence`, `dropped`, and `window` (null when no new window exists).
Each window has `sequence`, `uptime_ms`, and four `fans` entries with the fields below.
If the cursor is too old or ahead of the current sequence, `dropped` is true and the client
must fail the capture rather than silently export incomplete data. Reads do not clear history.

`TachDiagnosticsSerial` in [HardwareConfig.h](include/HardwareConfig.h) optionally enables
the same measurements on serial at 115200 baud; it is **disabled by default**.
When enabled, each update emits one line per fan:

```text
TACH,fan,window_us,pulses,intervals,min_us,max_us,span_us,rpm_count,rpm_period,rpm_display,pwm_pct
TACH,1,1000500,40,39,25000,25000,975000,1199.40,1200.00,1200.00,45.00
```

- `window_us`: measured time between counter snapshots, timestamped with `micros()` inside
  the same critical section as the counter reset. Each fan has its own window.
- `pulses`: all falling edges in that window. No debounce, rejection, or smoothing is applied.
- `intervals`: number of complete falling-edge-to-falling-edge intervals ending in the window.
  The first edge after startup only establishes a timestamp. The previous edge is retained
  across window boundaries, but forgotten after an empty window to avoid stale restart periods.
- `min_us`, `max_us`, `span_us`: shortest, longest, and sum of these intervals, in microseconds.
  Min/max are zero when `intervals` is zero.
- `rpm_count`: `pulses * 60000000 / window_us / pulses_per_revolution`.
- `rpm_period`: `intervals * 60000000 / span_us / pulses_per_revolution`. This averages over
  all measured periods, including anomalies; it is **not** a filtered or noise-immune RPM.
  `NA` in CSV (JSON null over BLE) means no usable period, not a measured zero speed. A single edge can produce a period
  if a previous edge is available from the preceding window.
- `rpm_display`: existing millisecond-window calculation used by BLE and the Windows app,
  used until 1.4.2; starting in 1.4.3 this field contains the corrected dashboard RPM.
  `rpm_raw_display` retains the original millisecond calculation. `pwm_pct` is the applied duty, including startup boost/safety override.
  RPM fields use `NA` if their denominator is unavailable or invalid.

All fan counters are snapshotted before serial output, and no formatting or logging runs
inside an interrupt. Set `TachDiagnosticsSerial` to `false` and rebuild to disable serial
output; edge timing and BLE history still run. This instrumentation adds interrupt work,
BLE transfers can affect scheduling, and optional serial output can delay the main loop,
so it is a diagnostic tool, not a hardware waveform capture.

Firmware **1.4.2** adds optional anomaly traces to each BLE fan entry. After four prior
intervals are available, an interval **strictly shorter than 75% of the preceding interval**
triggers capture of nine consecutive intervals: four before, the trigger at zero-based index
4, and four after. `trace_us` contains these durations in microseconds, `trace_trigger_us`
is the triggering edge's wrapping 32-bit microsecond timestamp, and `trace_reference_us`
is the immediately preceding duration (index 3). All three fields are absent without a
complete trace. This is a relative trigger, not a classification of electrical noise.

Capture can span RPM window boundaries; the trace is attached to the window in which it is
copied after completion, not necessarily the triggering window. Only the first complete trace
per fan between snapshots is retained; later anomalies still count in RPM/min/max but do not
overwrite it. An empty window clears preceding intervals and abandons an incomplete trace.
Retrieving a retained snapshot again returns the same trace. Serial diagnostic rows remain
unchanged; use the updated Windows app's CSV export to obtain the additional trace fields.

Compare indices 4 and 5 and their sum with indices 0-3 and 6-8. Two short intervals whose sum
matches the normal period support the extra-edge hypothesis. No short or zero interval is
rejected. A trace is not a waveform and cannot distinguish signal faults from ISR timing
effects or all variations in fan-generated pulse spacing.

Firmware **1.4.3** uses reconstructed-period RPM for BLE status and therefore the dashboard
and RPM history. The raw `pulses`, `intervals`, `span_us`, `rpm_count`, `rpm_period`, and
anomaly traces are unchanged. Added diagnostic fields are `corrected_intervals`,
`corrected_span_us`, `merged_pairs`, `rpm_corrected`, and `rpm_raw_display`.

The reference is the median of five resolved periods; all five must be within +/-10% of
that reference. Two consecutive intervals are merged only if each is strictly below 75%
of the reference, their sum is within +/-10%, and the next interval also returns within
+/-10% of the reference. Otherwise both intervals are kept separately. This guards against
sustained acceleration being mistaken for added edges. Candidates and their confirmation
can cross update windows; up to two intervals remain pending until the next edge. Resolved
periods are attributed to their resolution window, so their count need not match raw
interval count in that window. An empty raw window resets the reference and pending state.

Since **1.4.6**, up to four consecutive short pairs can be held pending. Every pair's
sum must meet the same reference tolerance, and a normal following interval confirms the
whole run. A mismatching interval or a fifth pair causes all pending intervals to be kept
as raw periods, without merging. This bounds the delay and preserves sustained acceleration;
up to eight intervals can now span measurement windows. The raw diagnostics remain unchanged.

Displayed RPM averages the resolved periods' durations, without a moving-average display
filter. When no nonzero resolved duration is available (startup, no edges, or pending data),
the original counting estimate is used; `rpm_corrected` is null, making that fallback
explicit in diagnostics. This heuristic can still misclassify a brief real speed excursion
or unusual rotor pulse spacing. It does not repair the electrical signal or PWM control.

For comparison, use a fixed PWM duty and wait for startup boost and motor transients to finish.
At 1200 RPM with two pulses/revolution, the average interval is 25000 us. Very short intervals
suggest extra edges; intervals near twice the normal period suggest missing edges. Neither is
proof of an electrical fault: rotor pulse spacing, real speed changes, and interrupt latency
also affect timings. Both estimators use the same edges and can agree even with a bad signal;
extra and missing pulses can cancel in the averages. With no oscilloscope, capture 30-60 seconds
of these lines at a stable duty and compare all fans. Do not disconnect cooling during a test.

## Persistent settings

Commands change the active RAM configuration. `save` writes both profiles and all four fan modes/duties to one NVS key and verifies the saved data. `defaults` changes RAM only; issue `save` to persist it. No flash writes occur during regular measurement or ordinary setting changes.

The `dirty` status indicates a change since the last load/save. Invalid or unavailable NVS data is reported without silently overwriting the stored key. A failed save does not roll back RAM changes.

## BLE protocol v1

Advertised name: `ESP32-FanController`; one client at a time.

| GATT item | UUID | Property |
|---|---|---|
| Service | `6e400001-b5a3-f393-e0a9-e50e24dcca9e` | UART service |
| RX (Windows → ESP32) | `6e400002-b5a3-f393-e0a9-e50e24dcca9e` | Write with GATT response |
| TX (ESP32 → Windows) | `6e400003-b5a3-f393-e0a9-e50e24dcca9e` | Notify |
| OTA data and acknowledgment | `6e400004-b5a3-f393-e0a9-e50e24dcca9e` | Encrypted write with/without response (without response since 1.4.0), notify; max 512 bytes |

Subscribe to TX before writing RX. Send UTF-8 JSON terminated by LF; CRLF is accepted. The maximum frame is 1,024 bytes. Fragment writes into 20-byte chunks for minimum-MTU compatibility. Wait for the complete LF-terminated response before sending the next command; only one request may be in flight. Notifications are byte fragments, not complete messages. Poll `get_status` after each response, typically every two seconds. On reconnect, subscribe again and reread configuration and status because a write may have executed before its response was lost.

Supported commands: `get_config`, `get_status`, `get_tach_diagnostics`, `set_profile`, `set_fan`, `save`, and `defaults`. `get_status` includes `ble_available` and `ble_status`, reflecting BLE initialization and advertising startup state. Failure details also remain available in the serial log. Example:

```json
{"cmd":"set_fan","fan":3,"mode":"fixed","duty":55}
```

Sensor and fan indexes start at 1. Invalid commands do not change configuration. See [CommandProcessor.cpp](src/CommandProcessor.cpp) and [Models.cs](../windows/src/FanController.Core/Models.cs) for protocol details.

### Firmware OTA

The encrypted OTA characteristic is separate from UART TX notifications. OTA commands still travel as newline-terminated JSON over UART RX, so subscribe to both UART TX and the OTA characteristic before starting. The OTA characteristic is restricted to the bonded, authorized owner. Firmware version 1.2.0 uses the explicit 4 MiB layout in [partitions.csv](partitions.csv): two 0x140000-byte OTA app slots, NVS, OTA metadata, and the remaining space for SPIFFS. Install 1.2.0 over serial once so its partition table is flashed; OTA is available for subsequent firmware updates.

The supported command sequence is:

1. `{"cmd":"ota_info"}` returns `data.protocol` (1), `data.max_size` (the inactive app partition size), and `data.running_image_sha256`.
2. `{"cmd":"ota_begin","size":N,"sha256":"<64 hex chars>"}` starts a transfer and returns `data.offset: 0`. The size and SHA-256 cover the **entire local `.bin` file**, including its final 32-byte appended image hash. This is integrity checking only; images are not digitally signed.
3. Write each binary packet to the OTA characteristic as a 4-byte little-endian absolute offset followed by 1–508 image bytes. Firmware 1.4.0 also advertises encrypted Write Without Response: use it when supported, otherwise use Write With Response. Keep one packet in flight; wait for its 5-byte notification acknowledgment before sending the next. The acknowledgment is `[status, acceptedOffset-le32]`: status 0 means the flash write succeeded; nonzero status indicates a rejected packet or flash failure. Offset and image-size errors abort the transfer.
4. `{"cmd":"ota_finish"}` verifies the full-file SHA-256 and ESP32 image structure with `esp_ota_end`, returning `data.size` and `data.sha256` (the verified **full-file** digest). It only stages the image; it does not select it for boot.
5. After explicit user confirmation, `{"cmd":"ota_activate"}` selects the staged image. The success response is fully notified before restart is scheduled, with a one-second delivery grace period. A disconnect before the complete activation response cancels activation and restores the previous boot selection. A disconnect after the full response does not cancel the scheduled restart. `{"cmd":"ota_abort"}` discards an active transfer or staged image before activation.

`running_image_sha256` is the ESP-IDF **image digest** from `esp_partition_get_sha256` for the running partition, not a digest of every byte in the `.bin` file. For an image with the appended-hash flag set, it corresponds to the SHA-256 embedded in the final 32 bytes (the digest of all preceding image bytes). After reconnecting to the updated device, compare this field with the validated appended digest; do not compare it with the full-file `ota_finish.data.sha256`.

Transfers time out after 30 seconds without an accepted data packet. Disconnect, a new authorized session, or loss of the OTA/UART notification subscription discards in-progress or staged firmware. `get_config`, `get_status`, and OTA commands remain available during transfer; configuration mutations are rejected while a transfer or staged image exists. Each binary packet is written in the main loop, not from a BLE callback. Sector erase/write and final image validation are synchronous flash operations and can temporarily pause thermal sampling/control; per-packet processing is bounded, but flash latency has no hard real-time guarantee. Keep independent thermal protection and do not rely on the firmware loop as the only safety measure.

### Pairing and security

- For the first PC, start the board normally and hold **BOOT (GPIO0) for 2 seconds**, then release. Pair from the Windows client within 60 seconds.
- One owner is stored in NVS; later connections reuse keys without pressing BOOT.
- With firmware **1.3.0 or later**, hold **BOOT for at least 5 seconds**, then release, to revoke the owner and immediately open a new 60-second pairing window. Remove the old Windows Bluetooth pairing before connecting; no second BOOT press is needed. Earlier firmware requires a 10-second hold to revoke, followed by a separate 2-second hold to enroll.
- On boards with an active-high **D2 LED on GPIO2**, the LED blinks while enrollment is open and flashes twice after successful revocation. Since firmware **1.4.0**, it stays on during an authorized encrypted connection and turns off on disconnection (unless enrollment is still open). The PWR LED is not controlled. `PairingLedPin` in [HardwareConfig.h](include/HardwareConfig.h) selects the indicator pin; verify the wiring for other boards.
- Do not hold BOOT during reset; it may enter the ROM bootloader.
- BLE LE Secure Connections **Just Works** encrypts the connection but does not provide MITM protection during initial pairing. Pair in a trusted location near the board.
- Firmware 1.1.3 added `ble_rssi_dbm` to status for the signal received by the ESP32. Firmware 1.2.0 adds explicit-confirmation OTA; `ble_rssi_dbm` is null when unavailable and prior samples are never reused.

## Dependencies

- [NimBLE-Arduino 2.5.2](https://github.com/h2zero/NimBLE-Arduino/releases/tag/2.5.2)
- [ArduinoJson 7.4.3](https://github.com/bblanchon/ArduinoJson/releases/tag/v7.4.3)

Versions are pinned in [platformio.ini](platformio.ini). [secure_nimble.py](scripts/secure_nimble.py) applies a reproducible local NimBLE pairing patch before build; review it whenever updating NimBLE.

## Build and test

Firmware 1.4.0 advertises an ATT MTU of 517, requests a 15–30 ms connection interval and
251-byte link-layer packets. Windows decides the actual negotiated values; the serial log
reports MTU and maximum OTA image bytes per packet. A default MTU of 23 permits only 16
image bytes; MTU 247 permits 240, and MTU 517 permits 508. Writes remain paced by flash
acknowledgments, not blind streaming. Installing 1.4.0 from 1.3.0 still uses the old firmware's
capabilities; measure the optimized path on a subsequent transfer. The serial log reports
verified byte count, transfer/validation duration and average KiB/s at `ota_finish` (excluding
initial flash erase, confirmation and restart). Only the image's required sectors are erased.

```powershell
pio run -e esp32dev
pio run -e esp32dev -t upload
pio device monitor -b 115200
```

Host C++ tests cover controller calculations, firmware command/storage behavior, and pairing logic. They require the ArduinoJson dependency restored by PlatformIO and a Visual Studio C++ developer shell. See [test/README](test/README) for PlatformIO's test runner documentation. Host tests do not replace ADC/PWM/TACH/BLE/NVS checks on the actual board.
