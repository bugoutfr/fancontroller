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

`main.cpp` wires these components together. BLE callbacks enqueue packets; commands and hardware changes are handled in the main loop. Mutable state belongs to component instances. The BLE v1 protocol and NVS format are stable.

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

Subscribe to TX before writing RX. Send UTF-8 JSON terminated by LF; CRLF is accepted. The maximum frame is 1,024 bytes. Fragment writes into 20-byte chunks for minimum-MTU compatibility. Wait for the complete LF-terminated response before sending the next command; only one request may be in flight. Notifications are byte fragments, not complete messages. Poll `get_status` after each response, typically every two seconds. On reconnect, subscribe again and reread configuration and status because a write may have executed before its response was lost.

Supported commands: `get_config`, `get_status`, `set_profile`, `set_fan`, `save`, and `defaults`. Example:

```json
{"cmd":"set_fan","fan":3,"mode":"fixed","duty":55}
```

Sensor and fan indexes start at 1. Invalid commands do not change configuration. See [CommandProcessor.cpp](src/CommandProcessor.cpp) and [Models.cs](../windows/src/FanController.Core/Models.cs) for protocol details.

### Pairing and security

- For the first PC, start the board normally and hold **BOOT (GPIO0) for 2 seconds**, then release. Pair from the Windows client within 60 seconds.
- One owner is stored in NVS; later connections reuse keys without pressing BOOT.
- Hold **BOOT for 10 seconds**, then release, to revoke the owner. Remove the Windows Bluetooth pairing before pairing again.
- Do not hold BOOT during reset; it may enter the ROM bootloader.
- BLE LE Secure Connections **Just Works** encrypts the connection but does not provide MITM protection during initial pairing. Pair in a trusted location near the board.
- Firmware 1.1.3 adds `ble_rssi_dbm` to status for the signal received by the ESP32. It is null when unavailable; prior samples are never reused.

## Dependencies

- [NimBLE-Arduino 2.5.2](https://github.com/h2zero/NimBLE-Arduino/releases/tag/2.5.2)
- [ArduinoJson 7.4.3](https://github.com/bblanchon/ArduinoJson/releases/tag/v7.4.3)

Versions are pinned in [platformio.ini](platformio.ini). [secure_nimble.py](scripts/secure_nimble.py) applies a reproducible local NimBLE pairing patch before build; review it whenever updating NimBLE.

## Build and test

```powershell
pio run -e esp32dev
pio run -e esp32dev -t upload
pio device monitor -b 115200
```

Host C++ tests cover controller calculations, firmware command/storage behavior, and pairing logic. They require the ArduinoJson dependency restored by PlatformIO and a Visual Studio C++ developer shell. See [test/README](test/README) for PlatformIO's test runner documentation. Host tests do not replace ADC/PWM/TACH/BLE/NVS checks on the actual board.
