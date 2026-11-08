# FanController Windows client

Local **WinUI 3 / .NET 10 desktop client**, currently targeting **Windows 11 x64**. It uses native Windows BLE APIs. No browser, cloud, backend, or application account is required. Normal measurements come from the ESP32 controller.

## Build, test, and run

From this directory in PowerShell:

```powershell
dotnet restore .\FanController.sln
dotnet build .\FanController.sln -c Release -p:Platform=x64
dotnet test .\tests\FanController.Tests\FanController.Tests.csproj -c Release
dotnet publish .\src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 --no-restore -o .\artifacts\desktop
.\artifacts\desktop\FanController.App.exe
```

For the combined build, public API documentation checks, host tests, and formatting verification:

```powershell
.\scripts\Test-CodeQuality.ps1
```

Production projects generate XML documentation and treat missing or malformed public API documentation as build errors.
Formatting follows `.editorconfig`; apply it with `dotnet format whitespace .\FanController.sln`.
Document public contracts, cancellation and ownership rather than adding comments that repeat implementation details.

The publish command uses the packages restored above and writes the Release application to `artifacts\desktop`.

### Windows installer

Build a self-contained **Windows 11 x64** setup EXE with Inno Setup 6:

```powershell
winget install --id JRSoftware.InnoSetup --exact --scope user
.\scripts\Build-Setup.ps1 -Version 1.0.0
```

The version is embedded in both the application and setup. The result is
`artifacts\installer\FanController-Setup-1.0.0-win-x64.exe`. If Inno Setup is installed
elsewhere, pass `-IsccPath 'C:\path\to\ISCC.exe'`.

The installer offers English/French UI, installs under
`%LOCALAPPDATA%\Programs\FanController` without administrator privileges, creates a Start
menu shortcut, and offers an optional desktop shortcut. .NET and Windows App SDK runtime
files are included; no separate runtime installation is required. Future setup versions
reuse the same application identity and installation directory.

Before installation or an upgrade, **quit** any running copy using the tray menu or the
application's Quit button; closing the window can leave it running in the tray.
Automatic startup remains opt-in from the application's settings after installation.
Uninstall removes the startup entry only if it points to the installation being removed,
and preserves `%LOCALAPPDATA%\FanController` (settings, remembered device, names and history).
The setup is not code-signed; Windows SmartScreen may warn about an unknown publisher.
Only run installers obtained from a trusted source. The Windows app version is separate
from the ESP32 firmware version.

Development run:

```powershell
dotnet run --project .\src\FanController.App\FanController.App.csproj -p:Platform=x64
```

Build prerequisites: .NET SDK **10.0.114** (`global.json`), Windows SDK/XAML tools, and Visual Studio 18 BuildTools. The Windows target SDK is 10.0.22621; minimum runtime is Windows 11 10.0.22000. Initial restore requires NuGet access.

Publishing is unpackaged, self-contained .NET and Windows App SDK. Distribute the complete `artifacts\desktop` directory; the PRI resource file, native DLLs, and language subdirectories are required. Windows Bluetooth drivers and system components are still required.

## Use

1. Flash the ESP32 firmware described in [../esp32/README.md](../esp32/README.md). Enable Bluetooth in Windows.
2. On first pairing, start the ESP32 normally and hold **BOOT for 2 seconds**, then release. Within 60 seconds, use **Search**, select `ESP32-FanController`, then **Connect / pair**.
3. The client subscribes to BLE notifications before reading configuration and status. It does not write settings during connection. It shows temperatures, sensor faults/safety state, four fan RPM readings, PWM, operating modes, boost, and PWM output state.
4. Edit both temperature profiles and each fan's automatic/fixed mode. Mode selectors display the current setting on first opening; changing the UI language updates their labels without replacing the choices. A profile requires 2–8 points, temperatures strictly increasing from −20 to 120 °C, and PWM from 0–100% non-decreasing. Fixed PWM fields are ignored in automatic mode. Firmware safety overrides remain active.
5. **Apply to RAM** changes behavior immediately. **Save to ESP32** persists the current RAM settings in NVS across restarts. These are separate operations.
6. **Discard changes** restores the last configuration read from the ESP32 without writing over BLE.

A multi-command update is not atomic. If the link fails, some changes may have taken effect. The app invalidates the session and never retries an interrupted write. After reconnecting, review the actual configuration and local draft before applying again. Draft edits are retained during disconnects but not after closing the app.

## TACH diagnostics without USB

Settings includes **Capture 30 s**, **Capture 60 s**, cancellation, and **Export CSV**, below firmware updates.

Firmware **1.4.7** stabilizes automatic PWM with 1 s rising / 3 s falling time constants.
For curves beginning at 0%, stopped fans restart 0.5 °C above the last zero-duty point
and stop immediately when the curve returns to zero. Fixed PWM, startup boost and thermal
safety bypass this filtering. Live target PWM and applied PWM may therefore differ even
outside startup boost; temperature and RPM graphs retain the measured values.
Install the diagnostic ESP32 firmware over the existing OTA connection first. Older firmware
remains usable for control; capture is disabled unless `get_status` advertises
`tach_diagnostics_supported: true`. No serial port or simultaneous USB/VIN power is required.

Apply a fixed PWM duty and let the fans stabilize before starting. The capture does not change
fan settings. It collects all four fans' consecutive, numbered one-second windows over BLE.
Normal telemetry/history polling, setting commands and OTA are suspended during capture;
the ESP32 continues regulating locally. Controls resume afterward. Interruptions, firmware
restarts detected by the cursor, and overwritten windows fail the capture explicitly. No
partial capture is presented as complete; a new capture clears the previous result. Cancellation
of an in-flight BLE request may require automatic reconnection.

After completion, export CSV using the save dialog. Export remains available after disconnect.
Columns include firmware sequence and uptime, fan number, actual window duration, pulse count,
interval count/minimum/maximum/sum, RPM by counting and by periods, existing displayed RPM,
and applied PWM. Units are microseconds and RPM; decimals use a point, absent period RPM is `NA`.
No filtering or smoothing is applied. Both estimators use the same edges and can agree despite
noise; inspect interval extremes as well. See the [firmware diagnostic reference](../esp32/README.md#tach-diagnostics).

With firmware **1.4.2**, the same CSV also exports `trace_trigger_us`, `trace_reference_us`,
and `trace_us`. The last column is a semicolon-separated sequence of nine intervals in
microseconds: four preceding intervals, the short triggering interval, and four following
intervals. `NA` means no complete anomaly trace for that fan/window (including older firmware).
The trigger is an interval strictly below 75% of its predecessor; it does not filter the data.
Compare the trigger and its following interval's sum against normal surrounding intervals.
The trace may span two measurement windows and is attached when complete. Firmware retains
only the first trace per fan between snapshots, not every anomaly.

Firmware **1.4.3** uses corrected period RPM for the dashboard and future RPM history.
The CSV retains raw estimates and appends `rpm_raw_display`, `rpm_corrected`,
`corrected_intervals`, `corrected_span_us`, and `merged_pairs`. Unavailable newer fields
are `NA` with older firmware. The correction only merges two short intervals after their
sum and the next interval match a stable recent period; it does not smooth the chart or
change fan control. Pending candidates may cross update windows. If no usable resolved
period exists, displayed RPM uses raw counting and `rpm_corrected` is `NA`.

Firmware **1.4.4** fixes diagnostic acquisition falling behind the expanded responses:
UART notifications now use the negotiated MTU and firmware retains 128 recent windows.
An overwritten cursor is explicitly distinguished from a backwards sequence after restart.
Capture failure invalidates the BLE session by design; this disconnection alone does not
indicate an ESP32 crash.

Firmware **1.4.6** extends correction to runs of up to four consecutive short pairs,
confirmed by the next normal interval. Invalid or longer runs remain unmerged. CSV fields
and raw measurements are unchanged; no application update is needed for this extension.

## Firmware update over Bluetooth

Install firmware **1.2.0 or later once over USB**, including its OTA-compatible partition table.
In Settings, select an OTA-capable FanController PlatformIO application `firmware.bin` (1.2.0 or later,
not a bootloader, partition table or merged image). Post-restart verification requires the new firmware's OTA service.
The image must target the classic ESP32 and fit the inactive **1.25 MiB** application partition.
The existing paired owner and encrypted BLE link are required. Old firmware remains usable with this client,
but the update button is disabled when its OTA characteristic is absent.

The client checks the embedded ESP image digest locally, transfers binary blocks with explicit flash-write
acknowledgments, then verifies the complete file SHA-256 returned by the controller.

Firmware 1.4.0 advertises Write Without Response for OTA, avoiding a redundant GATT response;
the client automatically uses it while still awaiting the explicit flash acknowledgment for every block.
Earlier firmware continues using Write With Response. Payload size follows the negotiated MTU,
up to 508 image bytes per block. Faster MTU/link settings require the new firmware to be running;
the first update from 1.3.0 uses its existing settings. The firmware serial log reports negotiated
MTU and verified transfer throughput, excluding initial erase, confirmation and restart.

Polling and setting commands are suspended during the operation. Progress describes bytes acknowledged, not successful activation.
The upload panel shows elapsed binary transfer seconds and mean throughput in KiB/s
(Kio/s in French), based only on bytes acknowledged by the ESP32. Timing starts after
`ota_begin`, updates during upload, and freezes after the final block. It excludes local
file loading, initial flash preparation, validation, confirmation, and restart/reconnection.
On interruption, the last elapsed duration and acknowledged throughput remain visible.
After validation, a separate confirmation offers **Activate and restart** or **Discard firmware**.
Save settings beforehand: unsaved RAM settings and uncommitted drafts are not automatically written.
Drafts are retained locally, but RAM settings are lost on restart.

After activation the client reconnects without re-pairing and compares the running partition's embedded image
digest with the selected image. Success is shown only after this verification, not merely after transfer.
Transient post-restart connection failures are retried for up to 90 seconds without re-pairing or
replaying OTA writes. If this verification window expires, the pending image digest is retained
for the next connection during the same application session; a matching digest then updates the
OTA result to success. A mismatched digest remains an error.
Interrupted writes are never retried or resumed; start a new update after reconnecting. Cancellation before
activation leaves the old boot partition selected; cancellation after activation cannot undo the restart.
Keep power connected. Regulation continues between flash operations, but flash erase/write can delay acquisition,
and resetting the MCU can briefly interrupt cooling. Do not update equipment that cannot tolerate this.
SHA-256 checks corruption, **not publisher authenticity**; only use trusted files.

## Automatic connection and tray behavior

After a successful initial read, the last controller address is stored in `%LOCALAPPDATA%\FanController\last-device.txt`. At startup, the app reconnects only to that controller if it remains paired in Windows. It never automatically chooses another device or opens a new pairing window.

After a link loss, retry delays increase from 2 to 30 seconds while the app is running. Reconnection resubscribes and rereads configuration/status; interrupted writes are never replayed. **Disconnect** or **Suspend reconnection** stops retries until a manual connect or the next application launch. A manual search also pauses retries.

Closing or minimizing the window hides it in the system tray; BLE acquisition continues while connected. Double-click the tray icon or choose **Open** to restore the window. **Quit** exits the app. The optional **Launch at Windows startup** setting starts minimized in the tray and requires no administrator privileges. Startup uses the current executable path in the per-user Windows Run key; keep the complete publish directory in place.

## Interface and history

The dashboard displays live readings and firmware version. Settings contains connection controls, profile charts, and fan modes. Chart segments show linear interpolation; in edit mode, drag points to adjust temperature and PWM. The profile axes remain fixed at −20…120 °C and 0…100%. The add button inserts a point into the widest temperature interval while preserving the curve shape.

Settings also lets you assign optional names to each probe and fan. These labels are saved locally in `%LOCALAPPDATA%\FanController\device-names.json`, appear on the dashboard and history charts, and are never sent to or saved by the ESP32.
The name editor keeps numbered references (Probe 1, Probe 2, Fan 1–4) in its left column, regardless of custom names.

History stores received readings locally as per-controller, per-day JSONL files under `%LOCALAPPDATA%\FanController\history`. It retains 30 days and supports 1 hour, 24 hours, 7 days, and 30 days. Data is recorded only while the app receives connected measurements; offline periods are not reconstructed. Files are not uploaded. Disk errors are shown and logged without stopping fan control or BLE acquisition.

The RSSI display reports signal strength received by the ESP32 from Windows and requires firmware **1.1.3 or later**. It is not the Windows scan signal. An unavailable reading is shown when unsupported or when the measurement fails.

## Architecture

- `FanController.Core`: protocol framing/session, controller transactions, connection/acquisition lifecycle,
  reconnect policy, validation, history storage, and plot series. It has no WinUI or Windows BLE dependencies.
- `FanController.App`: WinUI views/converters, Windows BLE adapters, tray/startup integration,
  and presentation models. `Services/DesktopServices.cs` is the composition root:
  storage paths, logging, dispatching, scanning and startup settings are supplied there rather than created by view models.
- `tests/FanController.Tests`: host tests using fake transports and stores. The portable view model sources
  are linked directly, as are their service contracts; only resource lookup is stubbed, not presentation logic.

`ControllerConnection` owns each transport, protocol session, request/acquisition lifetime and the reconnection task.
Disconnect and shutdown cancel operations, await their completion, then dispose session resources.
`HistoryViewModel` owns the shared history store and disposes it only after acquisition and its own reads have stopped.
Window shutdown awaits this sequence before closing; normal close/minimize still hides to the tray.
Never retry interrupted writes automatically: reconnecting only reads actual configuration and status.

View models expose booleans rather than WinUI visibility values and receive a UI dispatcher for background notifications.
Shared telemetry cards keep their identity and binding notifications when readings change.
Validation errors carry a resource key in exception metadata, leaving translation in the presentation layer;
there is no mutable global localization callback in Core.

History disk failures, command failures and language preference errors are reported without claiming success.
Missing optional localization keys use their fallback; technical resource lookup failures are logged once per error code.
Logs are best-effort so an error in diagnostics cannot cause a second application failure.

Host tests cover interrupted connections/writes, reconnection without pairing or replay,
shutdown during discovery/acquisition/history reads, retained drafts, command reentrancy, and preference failures.
They do not replace hardware tests of Windows BLE pairing or interactive WinUI/tray behavior.

The client uses .NET APIs, `System.Text.Json`, `INotifyPropertyChanged`, and `ICommand`; it has no third-party BLE or MVVM dependency. The WinUI and test package versions are pinned in the project files.

## Pairing security

BLE **LE Secure Connections Just Works** encrypts the link but does not provide man-in-the-middle protection on first pairing. Pair in a trusted location near the board. The physical BOOT action authorizes the first owner; the app cannot display an identity on the ESP32. With firmware 1.3.0 or later, holding BOOT for at least 5 seconds and releasing revokes the owner and immediately opens a 60-second pairing window; the GPIO2 D2 LED flashes twice, then blinks until enrollment succeeds or expires. Since 1.4.0, the LED stays on during an authorized encrypted connection and turns off on disconnection unless enrollment remains open. Remove the old Windows pairing before connecting. Earlier firmware requires a 10-second hold to revoke, then a separate 2-second hold to enroll.

## License

The repository is licensed under the [MIT License](../LICENSE). Third-party packages and Windows components retain their own licenses.
