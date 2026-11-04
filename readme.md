# FanController

Standalone four-channel PWM fan controller based on the ESP32, with a local Windows client for Bluetooth Low Energy configuration and monitoring. No cloud service, account, or backend is required.

## Features

- Two 10 kΩ / B3950 NTC sensors, each assigned to two fans.
- Independent temperature profiles with linear interpolation.
- Per-fan PWM override and tachometer (TACH) speed measurement.
- Thermal protection takes priority over manual settings.
- Settings are applied in RAM and saved explicitly to NVS for persistence across restarts.
- BLE pairing is physically authorized with the ESP32 BOOT button.
- Windows client with profile charts, local 30-day temperature/RPM history, and system-tray operation.
- Automatic reconnection to the last Windows-paired controller. Interrupted writes are never replayed.

Temperature control runs on the ESP32 without a computer or Bluetooth connection. The percentage controls PWM duty cycle; it is not closed-loop RPM control.

## Project layout

| Directory | Contents | Guide |
|---|---|---|
| [esp32](esp32/) | Arduino / PlatformIO firmware, hardware configuration, and C++ tests | [Firmware guide](esp32/README.md) |
| [windows](windows/) | WinUI 3 / .NET 10 client for Windows 11 x64 and .NET tests | [Windows client guide](windows/README.md) |
| [hardware](hardware/) | KiCad prototype board CAD | [Hardware guide](hardware/README.md) |

The KiCad board is explicitly a work in progress and is **not ready for fabrication**. It does not replace the firmware wiring diagram.

## Quick start

### ESP32

From `esp32`, with PlatformIO installed:

```powershell
pio run -e esp32dev
pio run -e esp32dev -t upload
pio device monitor -b 115200
```

Review the [wiring and power precautions](esp32/README.md#wiring) before uploading. Do not connect powered USB and 5 V to VIN together unless supply isolation has been confirmed.

### Windows

From `windows`, with the prerequisites in the [Windows guide](windows/README.md):

```powershell
dotnet restore .\FanController.sln
dotnet test .\tests\FanController.Tests\FanController.Tests.csproj -c Release
dotnet publish .\src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\desktop
.\artifacts\desktop\FanController.App.exe
```

Distribute the complete publish directory, not only the executable.

## First pairing

1. Start the ESP32 normally, hold **BOOT for 2 seconds**, then release to open a 60-second pairing window.
2. Search for the controller in the Windows client and connect/pair.
3. Subsequent connections reuse the stored keys.

Holding **BOOT for 10 seconds** and releasing revokes the owner. Remove the Windows pairing before pairing again. Do not hold BOOT during reset. **Just Works** encrypts the link but does not protect the first pairing against active interception; pair in a trusted environment.

## License

This project is distributed under the permissive [MIT License](LICENSE). Third-party dependencies retain their own licenses; see the relevant dependency documentation.

## Tests

- Firmware and pairing tests: [ESP32 guide](esp32/README.md#build-and-test).
- Windows tests: [Windows guide](windows/README.md#build-test-and-run).

Host tests do not replace checking sensors, PWM/TACH outputs, Bluetooth, and power behavior on real hardware.
