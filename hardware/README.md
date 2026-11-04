# KiCad prototype board (P1)

> **Work in progress — NOT FOR FABRICATION.** A connected schematic and fully routed PCB are available, but the SATA connector mechanics and electrical qualifications still need review. No manufacturing outputs are approved.

## Open the design

Open `cad\FanController.kicad_pro` in **KiCad 10.0.7**.

- `cad\FanController.kicad_sch`: native A2 schematic with power, USB-UART, BOOT/RESET, four PWM/TACH channels, and two NTC inputs.
- `cad\FanController.kicad_pcb`: native two-layer board, 74 × 54 mm outline, four M2 mounting holes, and 71 electrical components.
- `cad\FanController.pretty\SATA_Power_B_Provisional.kicad_mod`: provisional custom SATA footprint; its mechanical fit is not confirmed.
- `cad\FanController.net` and `connectivity.json`: exported netlist and shared schematic/PCB pin-to-net manifest.
- `cad\FanController-schematic.pdf`, `cad\preview\`: design views, not manufacturing files.
- `cad\prototype-bom.csv`: design bill of materials, not an approved assembly BOM.

## Reproducible validation

From the repository root:

```powershell
.\hardware\cad\validate.ps1
```

This regenerates and overwrites the CAD files, restoring validated copper from `cad\routing.json`; it does not run the exploratory autorouter. Pad geometry and nets must match the routing source. The script uses the Python bundled with KiCad and has no external Python dependencies.

To validate the existing PCB without regenerating it:

```powershell
.\hardware\cad\validate.ps1 -Existing
```

The checked-in KiCad 10.0.7 validation reports 0 ERC errors/warnings, 0 physical DRC errors/warnings, 0 unconnected items, and 0 schematic parity differences. These results verify CAD consistency only; they do not qualify the board for manufacturing or safe operation.

After manually changing copper, run DRC before capturing the new route source:

```powershell
& 'C:\Program Files\KiCad\10.0\bin\kicad-cli.exe' pcb drc FanController.kicad_pcb --format json --output drc.json --schematic-parity --refill-zones --save-board --exit-code-violations
```

Only capture routing after a clean DRC:

```powershell
& 'C:\Program Files\KiCad\10.0\bin\python.exe' routing.py --capture
```

## Design constraints and open checks

- Target enclosure interior: 80 × 60 × 20 mm; board: 74 × 54 mm.
- Target MCU: ESP32-WROOM-32E-N4, 4 MB flash, no PSRAM. GPIO assignments must match [firmware HardwareConfig.h](../esp32/include/HardwareConfig.h).
- Four keyed PC fan headers, 12 V, 0.5 A per output and 2 A continuous aggregate (3 A startup target); actual fan current must be checked.
- Two JST-XH connectors for 10 kΩ / B3950 NTCs and fixed 10 kΩ resistors.
- USB-C USB 2.0, UART, BOOT, RESET. No RGB LED.
- SATA Power 15-contact device-side, right-angle type B connector. Verify electrical orientation, opposing locator, full body outline, and current rating against the actual part.

| Function | GPIO | ESP32-WROOM-32E module pin |
|---|---:|---:|
| PWM1 / TACH1 | 15 / 13 | 23 / 16 |
| PWM2 / TACH2 | 16 / 34 | 27 / 6 |
| PWM3 / TACH3 | 17 / 14 | 28 / 13 |
| PWM4 / TACH4 | 18 / 27 | 30 / 12 |
| NTC1 / NTC2 | 33 / 32 | 9 / 8 |
| RX0 / TX0 | 3 / 1 | 34 / 35 |
| BOOT / EN | 0 / EN | 25 / 3 |

The fan outputs use a 3.3 V logic stage and open-collector transistor to provide 5 V-compatible PWM. TACH uses a 5.23 kΩ pull-up to 3.3 V and 100 pF filter; never pull TACH up to 5 V. NTC inputs use a 10 kΩ divider and 100 nF filter. GPIO12 is pulled down to preserve the 3.3 V flash strapping level.

Power input ORing prevents reverse power flow through the rails but is **not galvanic isolation**. The USB behavior with the host powered off, UART margins at minimum USB voltage, USB current before enumeration, and LDO thermal dissipation remain to be qualified. Via-in-pad is a hand-soldered prototype assumption, not an industrial filled/plugged via specification.

The ESP32 antenna keepout, enclosure clearances, actual connector fit, fuses, startup current, and thermal behavior must be checked on a physical prototype. A clean DRC does not make this design production-ready.
