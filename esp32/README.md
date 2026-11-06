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
- Startup boost runs at 100% for about one second, including when moving from 0% to a positive target.
- **Safety override:** a sensor fault or temperature at/above **80 °C** sets both associated fans to 100%, including fixed-mode fans. The requested mode is retained and resumes when the fault clears.
- Electrical sensor faults: ≤50 mV is `short_circuit`; ≥3,050 mV is `open_circuit`; calculated temperatures outside −20…120 °C are `out_of_range`. Faulted sensors report a null temperature.
- There is no RPM closed-loop control or automatic recovery when TACH reports zero. Hardware faults, power loss, or MCU reset can still interrupt cooling; use independent thermal protection where required.

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

Supported commands: `get_config`, `get_status`, `set_profile`, `set_fan`, `save`, and `defaults`. `get_status` includes `ble_available` and `ble_status`, reflecting BLE initialization and advertising startup state. Failure details also remain available in the serial log. Example:

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
