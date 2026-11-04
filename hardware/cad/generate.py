"""Rebuild the electrically connected KiCad prototype; never exports fabrication files.

Run using the Python distributed with KiCad 10. Native libraries supply pin
geometry; a shared manifest supplies both schematic labels and PCB pad nets.
"""
from __future__ import annotations

import copy
import csv
import heapq
import json
import math
from pathlib import Path
import re
import subprocess
import uuid

import pcbnew as pcb

HERE = Path(__file__).resolve().parent
SHARE = Path(r"C:\Program Files\KiCad\10.0\share\kicad")
NAME = "FanController"
ROOT = str(uuid.uuid5(uuid.NAMESPACE_URL, "FanController/prototype-P1"))
PARTS = []
SYMBOLS = {}
LIBRARIES = {}


def uid(text):
    return str(uuid.uuid5(uuid.UUID(ROOT), text))


def q(text):
    return json.dumps(str(text), ensure_ascii=False)


def parse(text):
    tokens = iter(re.findall(r'"(?:\\.|[^"\\])*"|[()]|[^\s()]+', text))

    def item(token):
        if token != "(":
            return token
        result = []
        for token in tokens:
            if token == ")":
                return result
            result.append(item(token))
        raise ValueError("Unterminated S-expression")

    return item(next(tokens))


def dump(node):
    if not isinstance(node, list):
        return node
    return "(" + " ".join(dump(x) for x in node) + ")"


def children(node, key):
    return [x for x in node if isinstance(x, list) and x[0] == key]


def child(node, key):
    return next(iter(children(node, key)), None)


def value(token):
    return json.loads(token) if token.startswith('"') else token


def library_symbol(lib_id):
    library, name = lib_id.split(":")
    if library not in LIBRARIES:
        tree = parse((SHARE / "symbols" / f"{library}.kicad_sym").read_text(encoding="utf-8"))
        LIBRARIES[library] = {value(x[1]): x for x in children(tree, "symbol")}
    node = copy.deepcopy(LIBRARIES[library][name])
    parent = child(node, "extends")
    if parent:
        base = library_symbol(f"{library}:{value(parent[1])}")
        old = value(base[1])
        properties = {value(x[1]) for x in children(node, "property")}
        inherited = [x for x in base[2:] if not (
            isinstance(x, list) and x[0] == "property" and value(x[1]) in properties)]
        for x in inherited:
            if isinstance(x, list) and x[0] == "symbol":
                x[1] = q(value(x[1]).replace(old + "_", name + "_", 1))
        node = [node[0], node[1]] + inherited + [
            x for x in node[2:] if isinstance(x, list) and x[0] in ("property", "symbol")]
    return node


def add(ref, lib_id, val, footprint, nets, pos, angle=0, sch=None, source=""):
    if lib_id not in SYMBOLS:
        SYMBOLS[lib_id] = library_symbol(lib_id)
    PARTS.append(dict(ref=ref, lib_id=lib_id, value=val, footprint=footprint,
                      nets={str(k): v for k, v in nets.items()}, pos=pos, angle=angle,
                      sch=sch, uuid=uid(ref), source=source))


RFP = "Resistor_SMD:R_0603_1608Metric"
CFP = "Capacitor_SMD:C_0603_1608Metric"
QFP = "Package_TO_SOT_SMD:SOT-23"
SFP = "Button_Switch_SMD:SW_SPST_TL3342"
TI_LVC = "https://www.ti.com/lit/ds/symlink/sn74lvc14a.pdf"
TI_LDO = "https://www.ti.com/lit/ds/symlink/tlv1117lv.pdf"
WCH = "https://www.wch-ic.com/downloads/CH340DS1_PDF.html"
NPN = "https://www.onsemi.com/pdf/datasheet/mmbt3904lt1-d.pdf"
ESP = "https://documentation.espressif.com/esp32-wroom-32e_esp32-wroom-32ue_datasheet_en.html"


def resistor(ref, val, a, b, pos, sch, angle=0):
    add(ref, "Device:R", val, RFP, {1: a, 2: b}, pos, angle, sch)


def capacitor(ref, val, net, pos, sch, size=CFP, angle=0):
    add(ref, "Device:C", val, size, {1: net, 2: "GND"}, pos, angle, sch)


def manifest():
    # SATA numbering is electrically standard, but physical P1 orientation
    # remains provisional until continuity is checked on the actual connector.
    sata = {i: None for i in range(1, 16)}
    sata.update({i: "GND" for i in (4, 5, 6, 10, 12)})
    sata.update({i: "+5V_SATA" for i in (7, 8, 9)})
    sata.update({i: "+12V_SATA" for i in (13, 14, 15)})
    add("J1", "Connector_Generic:Conn_01x15", "SATA_POWER_B_PROVISIONAL",
        "FanController:SATA_Power_B_Provisional", sata, (13, 24), -90, (37, 51),
        "https://ae-pic-a1.aliexpress-media.com/kf/S0ac0b9d23cc148d9b369529cda9130ef4.jpg")
    add("J2", "Connector:USB_C_Receptacle_USB2.0_16P", "GCT_USB4105-GF-A",
        "Connector_USB:USB_C_Receptacle_GCT_USB4105-xx-A_16P_TopMnt_Horizontal",
        {"A1": "GND", "A4": "USB_VBUS", "A5": "USB_CC1", "A6": "USB_D+",
         "A7": "USB_D-", "A8": None, "A9": "USB_VBUS", "A12": "GND",
         "B1": "GND", "B4": "USB_VBUS", "B5": "USB_CC2", "B6": "USB_D+",
         "B7": "USB_D-", "B8": None, "B9": "USB_VBUS", "B12": "GND", "SH": "GND"},
        (5, 12), -90, (143, 46), "https://gct.co/files/drawings/usb4105.pdf")
    for ref, val, a, b, pos, sch in [
        ("F1", "3.15A_T_1206_VERIFY_I2T", "+12V_SATA", "+12V_FAN", (17, 42), (86, 30)),
        ("F2", "1A_PTC_1206", "+5V_SATA", "SATA_5V_FUSED", (18, 31), (86, 50)),
        ("F3", "0.5A_PTC_1206", "USB_VBUS", "USB_5V_FUSED", (17, 15.5), (191, 30)),
    ]:
        add(ref, "Device:Fuse", val, "Fuse:Fuse_1206_3216Metric",
            {1: a, 2: b}, pos, 0, sch)
    for ref, a, pos, sch in [
        ("D1", "SATA_5V_FUSED", (24, 31.5), (86, 70)),
        ("D2", "USB_5V_FUSED", (17, 20.5), (191, 50)),
    ]:
        add(ref, "Device:D_Schottky", "SS14", "Diode_SMD:D_SMA",
            {1: "+5V_SYS", 2: a}, pos, 90 if ref == "D2" else 0, sch,
            "https://www.vishay.com/docs/88746/ss12.pdf")
    add("D3", "Device:D", "SMBJ15A_TVS", "Diode_SMD:D_SMB",
        {1: "+12V_FAN", 2: "GND"}, (59, 42), 0, (86, 91),
        "https://www.vishay.com/docs/88392/smbj.pdf")
    # TLV1117LV is NOT AMS1117. Identical pin numbers are reused for graphics
    # only; the cached/local library is renamed to the actual TI device below.
    add("U2", "Regulator_Linear:AMS1117-3.3", "TLV1117LV33DCYR",
        "Package_TO_SOT_SMD:SOT-223-3_TabPin2", {1: "GND", 2: "+3V3", 3: "+5V_SYS"},
        (23, 37), 180, (244, 45), TI_LDO)
    capacitor("C1", "100uF_25V", "+12V_FAN", (67, 38), (37, 102),
              "Capacitor_SMD:CP_Elec_6.3x7.7")
    capacitor("C2", "10uF_10V_X7R", "+5V_SYS", (22, 34.5), (244, 74),
              "Capacitor_SMD:C_0805_2012Metric")
    capacitor("C3", "22uF_6.3V_X5R", "+3V3", (23, 41), (244, 95),
              "Capacitor_SMD:C_0805_2012Metric")
    capacitor("C4", "100nF_16V", "+3V3", (23, 43), (244, 115))
    capacitor("C5", "100nF_16V", "+5V_SYS", (26, 24), (300, 48))
    capacitor("C6", "100nF_16V", "UART_V3", (26, 27), (300, 72))
    # CH340C is operated in 5 V mode, with its V3 regulator bypassed only
    # by C6. TXD is divided before reaching the non-5-V-tolerant ESP32.
    add("U3", "Interface_USB:CH340C", "CH340C",
        "Package_SO:SOIC-16_3.9x9.9mm_P1.27mm",
        {1: "GND", 2: "UART_TX_5V", 3: "UART_RX", 4: "UART_V3", 5: "USB_D+",
         6: "USB_D-", 7: None, 8: None, 9: None, 10: None, 11: None, 12: None,
         13: "UART_DTR", 14: "UART_RTS", 15: "GND", 16: "+5V_SYS"},
        (25, 24), 0, (351, 51), WCH)
    add("U4", "Power_Protection:USBLC6-2SC6", "USBLC6-2SC6",
        "Package_TO_SOT_SMD:SOT-23-6",
        {1: "USB_D+", 2: "GND", 3: "USB_D-", 4: "USB_D-", 5: "+5V_SYS", 6: "USB_D+"},
        (14, 12), 0, (191, 91), "https://www.st.com/resource/en/datasheet/usblc6-2.pdf")
    resistor("R1", "5.1k_1%", "USB_CC1", "GND", (10, 7), (143, 85))
    resistor("R2", "5.1k_1%", "USB_CC2", "GND", (10, 13), (143, 106))
    resistor("R3", "1k_1%", "UART_TX_5V", "ESP_RX0", (30, 29), (300, 97))
    resistor("R4", "2k_1%", "ESP_RX0", "GND", (32, 29), (300, 117))
    resistor("R5", "1k", "ESP_TX0", "UART_RX", (30, 23), (351, 96))
    esp_nets = {i: None for i in range(1, 40)}
    esp_nets.update({1: "GND", 2: "+3V3", 3: "ESP_EN", 6: "FAN2_TACH",
                    8: "NTC2_ADC", 9: "NTC1_ADC", 12: "FAN4_TACH", 13: "FAN3_TACH",
                    14: "GPIO12_STRAP", 15: "GND", 16: "FAN1_TACH", 23: "FAN1_GPIO_PWM",
                    25: "ESP_BOOT", 27: "FAN2_GPIO_PWM", 28: "FAN3_GPIO_PWM",
                    30: "FAN4_GPIO_PWM", 34: "ESP_RX0", 35: "ESP_TX0", 38: "GND", 39: "GND"})
    add("U1", "RF_Module:ESP32-WROOM-32E", "ESP32-WROOM-32E-N4",
        "RF_Module:ESP32-WROOM-32E", esp_nets, (43, 14), 0, (451, 61), ESP)
    capacitor("C7", "22uF_6.3V_X5R", "+3V3", (31.5, 15), (501, 30),
              "Capacitor_SMD:C_0805_2012Metric")
    capacitor("C8", "100nF_16V", "+3V3", (31.5, 17), (501, 52))
    resistor("R6", "10k", "+3V3", "ESP_EN", (32, 20), (501, 75))
    capacitor("C9", "1uF_10V_X7R", "ESP_EN", (32, 22), (501, 98))
    resistor("R7", "10k", "+3V3", "ESP_BOOT", (43, 30), (553, 30))
    resistor("R8", "10k", "GPIO12_STRAP", "GND", (34, 25), (553, 53))
    add("SW1", "Switch:SW_Push", "RESET", SFP, {1: "ESP_EN", 2: "GND"},
        (32, 38), 0, (451, 116))
    add("SW2", "Switch:SW_Push", "BOOT", SFP, {1: "ESP_BOOT", 2: "GND"},
        (42, 38), 0, (501, 119))
    # ESP32 DevKitC two-transistor interlock: both UART controls low cannot
    # hold EN and GPIO0 low simultaneously.
    for ref, base, emitter, collector, pos, sch in [
        ("Q5", "EN_BASE", "UART_RTS", "ESP_EN", (30, 33), (451, 157)),
        ("Q6", "BOOT_BASE", "UART_DTR", "ESP_BOOT", (39, 31), (501, 157)),
    ]:
        add(ref, "Transistor_BJT:MMBT3904", "MMBT3904LT1G", QFP,
            {1: base, 2: emitter, 3: collector}, pos, 0, sch,
            NPN)
    resistor("R9", "10k", "UART_DTR", "EN_BASE", (28, 32), (451, 189))
    resistor("R10", "10k", "UART_RTS", "BOOT_BASE", (37, 31), (501, 189))
    # Four inversions followed by four collector-open inversions: HIGH = 100%.
    # LVC at 3.3 V avoids AHCT's 4.5 V minimum on diode-ORed 5 V supplies.
    logic_nets = {1: "FAN1_GPIO_PWM", 2: "INV1", 3: "FAN2_GPIO_PWM", 4: "INV2",
                  5: "FAN3_GPIO_PWM", 6: "INV3", 7: "GND", 8: "INV4",
                  9: "FAN4_GPIO_PWM", 10: None, 11: "GND", 12: None, 13: "GND", 14: "+3V3"}
    add("U5", "74xx:74HC14", "SN74LVC14APWR", "Package_SO:TSSOP-14_4.4x5mm_P0.65mm",
        logic_nets, (50, 37), 90,
        {1: (38, 183), 2: (143, 183), 3: (248, 183), 4: (353, 183),
         5: (451, 231), 6: (501, 231), 7: (553, 165)}, TI_LVC)
    capacitor("C10", "100nF_16V", "+3V3", (46, 37), (553, 210))
    for n, x in enumerate((12, 27, 42, 57), 1):
        sx = 38 + (n - 1) * 105
        add(f"J{n+2}", "Connector_Generic:Conn_01x04", f"FAN{n}_Molex_47053-1000",
            "Connector:FanPinHeader_1x04_P2.54mm_Vertical",
            {1: "GND", 2: "+12V_FAN", 3: f"FAN{n}_TACH", 4: f"FAN{n}_PWM"},
            (x, 49), 0, (sx, 272),
            "https://www.molex.com/en-us/products/part-detail/470531000")
        add(f"Q{n}", "Transistor_BJT:MMBT3904", "MMBT3904LT1G", QFP,
            {1: f"BASE{n}", 2: "GND", 3: f"FAN{n}_PWM"},
            (x + 7, 44), 0, (sx, 217),
            NPN)
        resistor(f"R{10+n}", "10k", "+3V3", f"FAN{n}_GPIO_PWM",
                 (43 + n * 3, 32), (sx+48, 155))
        resistor(f"R{14+n}", "2.2k", f"INV{n}", f"BASE{n}",
                 (x + 6, 41), (sx+48, 185))
        resistor(f"R{18+n}", "100k", f"BASE{n}", "GND",
                 (x + 9, 44), (sx+48, 215))
        resistor(f"R{22+n}", "5.23k_1%", "+3V3", f"FAN{n}_TACH",
                 (x + 6, 46.5), (sx+48, 250))
        capacitor(f"C{10+n}", "100pF_C0G", f"FAN{n}_TACH",
                  (x + 9, 46.5), (sx+48, 273))
    for n, y in enumerate((16, 27), 1):
        add(f"J{6+n}", "Connector_Generic:Conn_01x02", f"NTC{n}_10k_B3950",
            "Connector_JST:JST_XH_B2B-XH-A_1x02_P2.50mm_Vertical",
            {1: f"NTC{n}_ADC", 2: "GND"}, (67, y), 90,
            (451 + (n - 1) * 74, 287),
            "https://www.jst-mfg.com/product/pdf/eng/eXH.pdf")
        resistor(f"R{26+n}", "10k_1%", "+3V3", f"NTC{n}_ADC",
                 (62, y), (451 + (n-1)*74, 319))
        capacitor(f"C{14+n}", "100nF_16V", f"NTC{n}_ADC",
                  (62, y+2), (451+(n-1)*74, 342))


def renamed_symbols():
    replacements = {
        "Regulator_Linear:AMS1117-3.3": ("TLV1117LV33", TI_LDO),
        "74xx:74HC14": ("SN74LVC14A", TI_LVC),
    }
    local = []
    for old_id, (new, source) in replacements.items():
        node = SYMBOLS.pop(old_id)
        old = value(node[1])
        node[1] = q(new)
        for x in children(node, "symbol"):
            x[1] = q(value(x[1]).replace(old + "_", new + "_", 1))
        for x in children(node, "property"):
            if value(x[1]) == "Value":
                x[2] = q(new)
            if value(x[1]) == "Datasheet":
                x[2] = q(source)
        new_id = f"FanController:{new}"
        SYMBOLS[new_id] = node
        local.append(dump(node))
        for part in PARTS:
            if part["lib_id"] == old_id:
                part["lib_id"] = new_id
    (HERE / "FanController.kicad_sym").write_text(
        '(kicad_symbol_lib (version 20241209) (generator "kicad_symbol_editor")\n'
        + "\n".join(local) + ")\n", encoding="utf-8")
    (HERE / "sym-lib-table").write_text(
        '(sym_lib_table (version 7) (lib (name "FanController")(type "KiCad")'
        '(uri "${KIPRJMOD}/FanController.kicad_sym")(options "")(descr "Prototype exact parts")))\n',
        encoding="utf-8")


def symbol_pins(node, unit):
    result = []
    for sub in children(node, "symbol"):
        suffix = value(sub[1]).rsplit("_", 2)
        if int(suffix[-2]) in (0, unit):
            result.extend(children(sub, "pin"))
    return result


def schematic():
    embedded = []
    for lib_id, node in SYMBOLS.items():
        node = copy.deepcopy(node)
        node[1] = q(lib_id)
        embedded.append(dump(node))
    output = [
        f'(kicad_sch (version 20250114) (generator "eeschema") (uuid {ROOT}) (paper "A2")',
        '(title_block (title "FanController P1 - ELECTRICAL PROTOTYPE / NOT FOR FABRICATION")'
        ' (rev "P1-WIP") (comment 1 "SATA P1 orientation and second locator unverified"))',
        "(lib_symbols\n" + "\n".join(embedded) + ")",
    ]
    for part in PARTS:
        node = SYMBOLS[part["lib_id"]]
        units = sorted({int(value(sub[1]).rsplit("_", 2)[-2])
                        for sub in children(node, "symbol")} - {0}) or [1]
        for unit in units:
            sx, sy = part["sch"][unit] if isinstance(part["sch"], dict) else part["sch"]
            sx, sy = round(sx/1.27)*1.27, round(sy/1.27)*1.27
            sym_uuid = part["uuid"] if unit == 1 else uid(f'{part["ref"]}:{unit}')
            props = []
            for name, val, py, hidden in [
                ("Reference", part["ref"], sy - 8.89, False),
                ("Value", part["value"], sy - 6.35, False),
                ("Footprint", part["footprint"], sy, True),
                ("Datasheet", part["source"], sy, True),
            ]:
                px = sx
                justify = ""
                if not hidden:
                    native = next((p for p in children(node, "property") if value(p[1]) == name), None)
                    if native is not None:
                        at = child(native, "at")
                        px, py = sx+float(at[1]), sy-float(at[2])
                        native_justify = child(child(native, "effects"), "justify")
                        justify = dump(native_justify) if native_justify else ""
                props.append(f'(property {q(name)} {q(val)} (at {px} {py} 0)'
                             f' (effects (font (size 1.0 1.0)) {justify} {"(hide yes)" if hidden else ""}))')
            pins = symbol_pins(node, unit)
            output.append(
                f'(symbol (lib_id {q(part["lib_id"])}) (at {sx} {sy} 0) (unit {unit})'
                f' (in_bom yes) (on_board yes) (dnp no) (uuid {sym_uuid}) '
                + " ".join(props)
                + f'(instances (project "FanController" (path "/{ROOT}"'
                f' (reference {q(part["ref"])}) (unit {unit})))))'
            )
            for pin in pins:
                number = value(child(pin, "number")[1])
                numbers = number.strip("[]").split(",")
                pin_nets = {part["nets"].get(n) for n in numbers}
                if len(pin_nets) != 1:
                    raise RuntimeError(f'{part["ref"]}: inconsistent stacked pin nets {numbers}')
                net = pin_nets.pop()
                at = child(pin, "at")
                px, py, angle = float(at[1]), float(at[2]), int(at[3])
                x, y = round(sx+px, 5), round(sy-py, 5)
                if net is None:
                    output.append(f'(no_connect (at {x} {y}) (uuid {uid(sym_uuid+":NC:"+number)}))')
                    continue
                # Label is beyond the pin, with a real short wire joining it.
                dx = {0: -2.54, 180: 2.54, 90: 0, 270: 0}[angle]
                dy = {0: 0, 180: 0, 90: 2.54, 270: -2.54}[angle]
                ex, ey = round(x+dx, 5), round(y+dy, 5)
                output.append(
                    f'(wire (pts (xy {x} {y}) (xy {ex} {ey}))'
                    f' (stroke (width 0) (type default)) (uuid {uid(sym_uuid+":W:"+number)}))')
                label_angle = 0 if angle in (0, 180) else 90
                justify = "right bottom" if angle == 0 else "left bottom"
                output.append(
                    f'(global_label {q(net)} (shape input) (at {ex} {ey} {label_angle})'
                    f' (effects (font (size 0.85 0.85)) (justify {justify}))'
                    f' (uuid {uid(sym_uuid+":L:"+number)}))')
    # Connectors, fuses and diodes are passive. Flags declare external sources,
    # not an ERC exclusion. +3V3 is driven by U2; UART_V3 by U3.
    flag = library_symbol("power:PWR_FLAG")
    flag[1] = q("power:PWR_FLAG")
    output[2] = output[2][:-1] + "\n" + dump(flag) + ")"
    for n, net in enumerate(("GND", "+12V_SATA", "+12V_FAN", "+5V_SATA", "USB_VBUS", "+5V_SYS"), 1):
        x, y = round((30+n*50)/1.27)*1.27, round(369/1.27)*1.27
        flag_uuid = uid("flag"+net)
        output.append(
            f'(symbol (lib_id "power:PWR_FLAG") (at {x} {y} 0) (unit 1)'
            f' (in_bom no) (on_board yes) (dnp no) (uuid {flag_uuid})'
            f' (property "Reference" "#FLG0{n}" (at {x} {y+3} 0) (effects (font (size 1 1)) (hide yes)))'
            f' (property "Value" "PWR_FLAG" (at {x} {y-4} 0) (effects (font (size 1 1))))'
            f' (instances (project "FanController" (path "/{ROOT}" (reference "#FLG0{n}") (unit 1)))))'
        )
        output.append(f'(global_label {q(net)} (shape input) (at {x} {y} 0)'
                      f' (effects (font (size 1 1)) (justify left bottom)) (uuid {uid("FL:"+net)}))')
    for text, x, y in [
        ("SATA P1/P15 physical orientation PROVISIONAL. P11 intentionally NC. No SATA 3.3 V use.", 25, 13),
        ("SATA 5V / USB diode OR -> TI 1A LDO. 12V only powers fans.", 224, 13),
        ("GPIO HIGH -> LVC inverter LOW -> NPN OFF -> fan PWM HIGH (100%).", 25, 133),
        ("External 10k PWM pullups default HIGH during reset. 5.23k TACH pullups -> 3V3 only.", 25, 140),
        ("NTC wiring: 3V3 -- fixed 10k -- ADC -- external 10k/B3950 NTC -- GND.", 420, 266),
        ("CH340C 5V mode: V3 capacitor only. R3/R4 divide TXD; Q5/Q6 interlock auto BOOT/RESET.", 420, 205),
        ("WIP: no manufacturing exports; connector continuity and current ratings require validation.", 25, 396),
    ]:
        output.append(f'(text {q(text)} (at {x} {y} 0)'
                      f' (effects (font (size 1.05 1.05)) (justify left bottom)) (uuid {uid(text)}))')
    (HERE / f"{NAME}.kicad_sch").write_text("\n".join(output) + "\n)\n", encoding="utf-8")


def sata_footprint():
    folder = HERE / "FanController.pretty"
    folder.mkdir(exist_ok=True)
    items = [
        '(footprint "SATA_Power_B_Provisional" (version 20241229) (generator "pcbnew") (layer "F.Cu")',
        '(descr "PROVISIONAL AliExpress variant B; reference drawing S0ac0b9d23cc148d9b369529cda9130ef4 ONLY; P1 and opposite locator unknown")',
        '(attr through_hole)',
        '(property "Reference" "REF**" (at 8.89 2.5 0) (layer "F.SilkS") (effects (font (size 1 1) (thickness 0.15))))',
        '(property "Value" "SATA_B_PROVISIONAL" (at 8.89 -14 0) (layer "F.Fab") (effects (font (size 1 1) (thickness 0.15))))',
    ]
    for i in range(15):
        items.append(f'(pad "{i+1}" thru_hole oval (at {i*1.27:.2f} 0) (size 1.05 1.60)'
                     ' (drill 0.80) (layers "*.Cu" "*.Mask"))')
    items.append('(pad "" np_thru_hole circle (at -4.91 7.92) (size 1.2 1.2)'
                 ' (drill 1.2) (layers "*.Cu" "*.Mask"))')
    # No guessed body width or mirrored mounting hole is added.
    for start, end in [((0, 0), (17.78, 0))]:
        items.append(f'(fp_line (start {start[0]} {start[1]}) (end {end[0]} {end[1]})'
                     ' (stroke (width 0.15) (type dash)) (layer "F.Fab"))')
    items.append('(fp_text user "P1? CHECK / SECOND LOCATOR UNKNOWN / DEPTH 12mm" (at 8.89 5 0)'
                 ' (layer "F.Fab") (effects (font (size 0.8 0.8) (thickness 0.12))))')
    (folder / "SATA_Power_B_Provisional.kicad_mod").write_text("\n".join(items)+")\n", encoding="utf-8")
    (HERE / "fp-lib-table").write_text(
        '(fp_lib_table (version 7) (lib (name "FanController") (type "KiCad")'
        '(uri "${KIPRJMOD}/FanController.pretty") (options "") (descr "Provisional SATA drawing 1")))\n',
        encoding="utf-8")


def mm(value):
    return pcb.FromMM(value)


def point(x, y):
    return pcb.VECTOR2I(mm(x), mm(y))


def board():
    b = pcb.BOARD()
    b.SetCopperLayerCount(2)
    b.GetDesignSettings().SetBoardThickness(mm(1.6))
    nets = {}
    for name in sorted({n for p in PARTS for n in p["pad_nets"].values() if n}):
        net = pcb.NETINFO_ITEM(b, name)
        b.Add(net)
        nets[name] = net
    for part in PARTS:
        lib, name = part["footprint"].split(":")
        folder = HERE / f"{lib}.pretty" if lib == "FanController" else SHARE / "footprints" / f"{lib}.pretty"
        fp = pcb.FootprintLoad(str(folder), name)
        if fp is None:
            raise RuntimeError(f'Footprint unavailable: {part["footprint"]}')
        fp.SetFPID(pcb.LIB_ID(lib, name))
        fp.SetReference(part["ref"])
        fp.SetValue(part["value"])
        fp.GetField("Datasheet").SetText(part["source"])
        fp.SetPath(pcb.KIID_PATH(f'/{ROOT}/{part["uuid"]}'))
        fp.SetPosition(point(100+part["pos"][0], 100+part["pos"][1]))
        fp.SetOrientationDegrees(part["angle"])
        fp.Reference().SetTextSize(point(1, 1))
        fp.Reference().SetTextThickness(mm(0.12))
        fp.Reference().SetLayer(pcb.F_Fab)
        fp.Value().SetVisible(False)
        present = set()
        for pad in fp.Pads():
            number = pad.GetNumber()
            if number:
                present.add(number)
            net = part["pad_nets"].get(number)
            if net:
                pad.SetNet(nets[net])
        # All connected logical pin numbers must exist physically.
        missing = {k for k, v in part["nets"].items() if v} - present
        if missing:
            raise RuntimeError(f'{part["ref"]}: connected pins missing from footprint: {missing}')
        b.Add(fp)
        part["fp"] = fp
    for n, (x, y) in enumerate(((4, 4), (70, 4), (4, 50), (70, 50)), 1):
        fp = pcb.FootprintLoad(str(SHARE / "footprints" / "MountingHole.pretty"),
                               "MountingHole_2.2mm_M2")
        fp.SetReference(f"H{n}")
        fp.SetValue("M2_NPTH_2.2mm")
        fp.SetBoardOnly(True)
        fp.SetPosition(point(100+x, 100+y))
        fp.Value().SetVisible(False)
        if n == 4:
            fp.Reference().SetPosition(point(170, 153))
            fp.Reference().SetTextSize(point(0.8, 0.8))
        b.Add(fp)
    for a, c in [((100, 100), (174, 100)), ((174, 100), (174, 154)),
                 ((174, 154), (100, 154)), ((100, 154), (100, 100))]:
        line = pcb.PCB_SHAPE()
        line.SetShape(pcb.SHAPE_T_SEGMENT)
        line.SetStart(point(*a))
        line.SetEnd(point(*c))
        line.SetLayer(pcb.Edge_Cuts)
        line.SetWidth(mm(0.05))
        b.Add(line)
    for text, x, y, layer, size in [
        ("FanController P1 WIP", 143, 151.5, pcb.B_SilkS, 1),
        ("NOT FOR FABRICATION", 143, 153, pcb.B_SilkS, 1),
        ("ANTENNA: NO COPPER / METAL / CABLE", 143, 104, pcb.Dwgs_User, 0.8),
        ("SATA P1? VERIFY", 108, 144, pcb.F_SilkS, 0.8),
        ("12V FAN ONLY", 143, 153, pcb.F_SilkS, 0.8),
    ]:
        label = pcb.PCB_TEXT(b)
        label.SetText(text)
        label.SetPosition(point(x, y))
        label.SetLayer(layer)
        label.SetTextSize(point(size, size))
        label.SetTextThickness(mm(0.12))
        if layer == pcb.B_SilkS:
            label.SetMirrored(True)
        b.Add(label)
    pack_passives(b)
    return b


def pack_passives(b):
    """Place small parts near their circuit, outside real footprint courtyards."""
    fixed = {"U1", "U2", "U3", "U4", "U5", "J1", "J2", "J3", "J4", "J5", "J6",
             "J7", "J8", "SW1", "SW2", "F1", "F2", "F3", "D1", "D2", "D3", "C1",
             "H1", "H2", "H3", "H4"}

    def box(fp):
        fp.BuildCourtyardCaches()
        outline = fp.GetCourtyard(pcb.F_Cu)
        bb = outline.BBox() if outline.OutlineCount() else fp.GetBoundingBox(False, False)
        return (pcb.ToMM(bb.GetX()), pcb.ToMM(bb.GetY()),
                pcb.ToMM(bb.GetRight()), pcb.ToMM(bb.GetBottom()))

    def overlap(a, c, gap=0.1):
        return a[0] < c[2]+gap and a[2] > c[0]-gap and a[1] < c[3]+gap and a[3] > c[1]-gap

    occupied = [(f.GetReference(), box(f)) for f in b.GetFootprints() if f.GetReference() in fixed]
    # This reserves space, not a claimed connector body outline.
    occupied.append(("SATA_access_reservation", (100, 122, 113.8, 144)))
    occupied.extend([
        ("12V_input_landing", (113.8, 140.3, 115.2, 142.7)),
        ("12V_output_landing", (117.7, 141.2, 120.8, 142.8)),
        ("TVS_landing", (154.3, 141.3, 155.7, 142.7)),
    ])
    antenna = (119, 100, 167, 107.44)
    for fp in sorted((f for f in b.GetFootprints() if f.GetReference() not in fixed),
                     key=lambda f: (0 if f.GetReference().startswith("Q") else 1, f.GetReference())):
        original = fp.GetPosition()
        ox, oy = pcb.ToMM(original.x), pcb.ToMM(original.y)
        candidates = sorted((dx*dx+dy*dy, dx, dy)
                            for dx in range(-50, 51) for dy in range(-50, 51))
        placed = False
        for _, dx, dy in candidates:
            fp.SetPosition(point(ox+dx*0.5, oy+dy*0.5))
            bb = box(fp)
            if bb[0] < 100.5 or bb[2] > 173.5 or bb[1] < 100.5 or bb[3] > 153.5:
                continue
            if overlap(bb, antenna):
                continue
            if any(overlap(bb, other) for _, other in occupied):
                continue
            occupied.append((fp.GetReference(), bb))
            placed = True
            break
        if not placed:
            raise RuntimeError(f"Could not place {fp.GetReference()} without courtyard overlap")
        part = next(p for p in PARTS if p["ref"] == fp.GetReference())
        part["pos"] = [pcb.ToMM(fp.GetPosition().x)-100, pcb.ToMM(fp.GetPosition().y)-100]
    print("Small parts placed outside courtyards, antenna and power landings.")


def local_route(b):
    """Conservative local F.Cu routing only; unfinished global nets stay visible.

    Pads of other nets are obstacles. Accepted traces reserve their clearance
    for later nets. There is no hidden zero-clearance routing or DRC exclusion.
    """
    step = 0.25
    nx, ny = 297, 217
    pads = list(p for f in b.GetFootprints() for p in f.Pads())
    pads.extend(t for t in b.GetTracks() if isinstance(t, pcb.PCB_VIA))
    tracks = [(t.GetNetname(), pcb.ToMM(t.GetWidth()),
               (pcb.ToMM(t.GetStart().x), pcb.ToMM(t.GetStart().y)),
               (pcb.ToMM(t.GetEnd().x), pcb.ToMM(t.GetEnd().y)))
              for t in b.GetTracks() if not isinstance(t, pcb.PCB_VIA) and t.GetLayer() == pcb.F_Cu]
    keepout = (119, 100, 167, 107.44)
    count = 0
    failed = 0

    def grid(x, y):
        return round((x-100)/step), round((y-100)/step)

    def coords(node):
        return 100+node[0]*step, 100+node[1]*step

    def pad_xy(pad):
        return pcb.ToMM(pad.GetPosition().x), pcb.ToMM(pad.GetPosition().y)

    # Smallest local nets first; larger rails need explicit global routing.
    groups = {}
    for pad in pads:
        if pad.GetNetCode():
            groups.setdefault(pad.GetNetname(), []).append(pad)
    tasks = []
    for net, netpads in groups.items():
        edges = sorted((math.dist(pad_xy(a), pad_xy(c)), i, j)
                       for i, a in enumerate(netpads) for j, c in enumerate(netpads) if i < j)
        parent = list(range(len(netpads)))

        def root(i):
            while parent[i] != i:
                i = parent[i]
            return i

        for distance, i, j in edges:
            if root(i) != root(j):
                parent[root(j)] = root(i)
                if net not in ("GND", "+12V_FAN", "+12V_SATA") and distance <= 50:
                    tasks.append((distance, net, netpads[i], netpads[j]))
    for distance, net, a, c in sorted(tasks, key=lambda x: x[0]):
        width = (0.4 if net in ("+3V3", "+5V_SYS", "+5V_SATA", "USB_VBUS")
                 else 0.2 if net == "GND" else 0.25)
        margin = 0.21 + width/2
        blocked = set()
        for pad in pads:
            if pad.GetNetname() == net:
                continue
            bb = pad.GetBoundingBox()
            x0, y0 = pcb.ToMM(bb.GetX())-margin, pcb.ToMM(bb.GetY())-margin
            x1, y1 = pcb.ToMM(bb.GetRight())+margin, pcb.ToMM(bb.GetBottom())+margin
            i0, j0 = grid(x0, y0)
            i1, j1 = grid(x1, y1)
            blocked.update((i, j) for i in range(i0, i1+1) for j in range(j0, j1+1))
        for tn, tw, xy1, xy2 in tracks:
            if tn == net:
                continue
            length = math.dist(xy1, xy2)
            gap = 0.21+(tw+width)/2
            radius = math.ceil(gap/step)
            for k in range(max(1, math.ceil(length/step))+1):
                t = k/max(1, math.ceil(length/step))
                gi, gj = grid(xy1[0]+t*(xy2[0]-xy1[0]), xy1[1]+t*(xy2[1]-xy1[1]))
                blocked.update((gi+di, gj+dj) for di in range(-radius, radius+1)
                               for dj in range(-radius, radius+1)
                               if math.hypot(di, dj)*step <= gap+step/2)
        start, goal = grid(*pad_xy(a)), grid(*pad_xy(c))
        # Never override blocked start/goal: that indicates an unsuitable placement.
        queue = [(0, 0, start)]
        came = {}
        cost = {start: 0}
        reached = False
        while queue:
            _, spent, cur = heapq.heappop(queue)
            if cur == goal:
                reached = True
                break
            if spent != cost.get(cur):
                continue
            if len(cost) > 15000:
                break
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nxt = cur[0]+di, cur[1]+dj
                x, y = coords(nxt)
                if not (3 <= nxt[0] < nx-3 and 3 <= nxt[1] < ny-3):
                    continue
                if nxt in blocked or keepout[0] <= x <= keepout[2] and y <= keepout[3]:
                    continue
                ns = spent+1
                if ns < cost.get(nxt, 10**9):
                    cost[nxt] = ns
                    came[nxt] = cur
                    h = abs(nxt[0]-goal[0])+abs(nxt[1]-goal[1])
                    heapq.heappush(queue, (ns+h, ns, nxt))
        if not reached:
            failed += 1
            continue
        path = [goal]
        while path[-1] != start:
            path.append(came[path[-1]])
        path.reverse()
        reduced = [pad_xy(a)]
        for i in range(1, len(path)-1):
            before = path[i][0]-path[i-1][0], path[i][1]-path[i-1][1]
            after = path[i+1][0]-path[i][0], path[i+1][1]-path[i][1]
            if before != after:
                reduced.append(coords(path[i]))
        reduced.append(pad_xy(c))
        for xy1, xy2 in zip(reduced, reduced[1:]):
            if math.dist(xy1, xy2) < 0.001:
                continue
            track = pcb.PCB_TRACK(b)
            track.SetStart(point(*xy1))
            track.SetEnd(point(*xy2))
            track.SetWidth(mm(width))
            track.SetLayer(pcb.F_Cu)
            track.SetNetCode(a.GetNetCode())
            b.Add(track)
            tracks.append((net, width, xy1, xy2))
        count += 1
    print(f"Local routing: {count} connections, {failed} deferred; {len(tracks)} segments")
    return count, len(tracks)


def global_route(b, only_nets=None, include_ground=False):
    """Route remaining signal and low-current nets on both copper layers."""
    step = 0.25
    origin = 100.0
    clearance = 0.21
    ix_min, ix_max = 4, 292
    iy_min, iy_max = 4, 212
    keepout = (119, 100, 167, 107.44)
    layers = (pcb.F_Cu, pcb.B_Cu)
    connectivity = b.GetConnectivity()

    def xy(pos):
        return pcb.ToMM(pos.x), pcb.ToMM(pos.y)

    def grid(pos):
        return round((pos[0]-origin)/step), round((pos[1]-origin)/step)

    def grid_xy(node):
        return origin+node[0]*step, origin+node[1]*step

    def inside(node):
        x, y = grid_xy(node)
        return (ix_min <= node[0] <= ix_max and iy_min <= node[1] <= iy_max
                and not (keepout[0] <= x <= keepout[2] and y <= keepout[3]))

    def mark_disk(blocked, pos, radius):
        gi, gj = grid(pos)
        cells = math.ceil((radius+step*0.08)/step)
        for di in range(-cells, cells+1):
            for dj in range(-cells, cells+1):
                node = gi+di, gj+dj
                if (inside(node) and math.hypot(di, dj)*step <= radius+step*0.08):
                    blocked.add(node)

    def mark_segment(blocked, start, end, radius):
        length = math.dist(start, end)
        count = max(1, math.ceil(length/(step/2)))
        for i in range(count+1):
            t = i/count
            mark_disk(blocked, (start[0]+t*(end[0]-start[0]),
                                start[1]+t*(end[1]-start[1])), radius)

    def obstacles(net, width):
        blocked = {layer: set() for layer in layers}
        via_blocked = set()
        pads = [pad for fp in b.GetFootprints() for pad in fp.Pads()]
        for pad in pads:
            if pad.GetNetname() == net and pad.GetAttribute() != pcb.PAD_ATTRIB_PTH:
                continue
            bb = pad.GetBoundingBox()
            x0 = pcb.ToMM(bb.GetX())
            y0 = pcb.ToMM(bb.GetY())
            x1 = pcb.ToMM(bb.GetRight())
            y1 = pcb.ToMM(bb.GetBottom())
            pad_margin = clearance + width/2
            via_margin = clearance + 0.3
            x_start = max(ix_min, math.floor((x0-via_margin-origin)/step))
            x_end = min(ix_max, math.ceil((x1+via_margin-origin)/step))
            y_start = max(iy_min, math.floor((y0-via_margin-origin)/step))
            y_end = min(iy_max, math.ceil((y1+via_margin-origin)/step))
            for node in range(x_start, x_end+1):
                x = origin+node*step
                if x < x0-via_margin or x > x1+via_margin:
                    continue
                for row in range(y_start, y_end+1):
                    y = origin+row*step
                    if (y0-via_margin <= y <= y1+via_margin
                            and (pad.GetNetname() != net
                                 or pad.GetAttribute() == pcb.PAD_ATTRIB_PTH)):
                        via_blocked.add((node, row))
                    if (pad.GetNetname() != net
                            and x0-pad_margin <= x <= x1+pad_margin
                            and y0-pad_margin <= y <= y1+pad_margin):
                        if pad.GetLayerSet().Contains(pcb.F_Cu):
                            blocked[pcb.F_Cu].add((node, row))
                        if pad.GetLayerSet().Contains(pcb.B_Cu):
                            blocked[pcb.B_Cu].add((node, row))

        for track in b.GetTracks():
            if isinstance(track, pcb.PCB_VIA):
                pos = xy(track.GetPosition())
                diameter = pcb.ToMM(track.GetWidth(pcb.F_Cu))
                mark_disk(via_blocked, pos, clearance+0.3+diameter/2)
                if track.GetNetname() != net:
                    for layer in layers:
                        mark_disk(blocked[layer], pos,
                                  clearance+width/2+diameter/2)
                continue
            if track.GetNetname() == net:
                continue
            other_width = pcb.ToMM(track.GetWidth())
            start, end = xy(track.GetStart()), xy(track.GetEnd())
            if track.GetLayer() in blocked:
                mark_segment(blocked[track.GetLayer()], start, end,
                             clearance+(width+other_width)/2)
                mark_segment(via_blocked, start, end,
                             clearance+0.3+other_width/2)
        return blocked, via_blocked

    def anchors(item):
        if isinstance(item, pcb.PCB_VIA):
            return [(xy(item.GetPosition()), layer, False) for layer in layers]
        if item.GetClass() == "PAD":
            pos = xy(item.GetPosition())
            has_via = any(isinstance(track, pcb.PCB_VIA)
                          and track.GetNetname() == item.GetNetname()
                          and math.dist(pos, xy(track.GetPosition())) < 0.001
                          for track in b.GetTracks())
            return [(pos, layer,
                     not item.GetLayerSet().Contains(layer) and not has_via)
                    for layer in layers]
        if item.GetClass() == "PCB_TRACK":
            layer = item.GetLayer()
            return [(xy(item.GetStart()), layer, False),
                    (xy(item.GetEnd()), layer, False)]
        return []

    def components(net):
        items = [pad for fp in b.GetFootprints() for pad in fp.Pads()
                 if pad.GetNetname() == net]
        items.extend(track for track in b.GetTracks() if track.GetNetname() == net)
        item_ids = [item.m_Uuid.AsString() for item in items]
        indices = {}
        for i, item_id in enumerate(item_ids):
            indices.setdefault(item_id, []).append(i)
        unseen = set(range(len(items)))
        result = []
        while unseen:
            seed = min(unseen)
            connected = connectivity.GetConnectedItems(items[seed])
            connected_ids = {item.m_Uuid.AsString() for item in connected}
            members = {i for item_id in connected_ids
                       for i in indices.get(item_id, ()) if i in unseen}
            if not members:
                members = {seed}
            unseen.difference_update(members)
            result.append([items[i] for i in members])
        return result

    def candidate_pairs(left, right):
        options = []
        for a in left:
            for pa, la, va in anchors(a):
                for c in right:
                    for pc, lc, vc in anchors(c):
                        options.append((math.dist(pa, pc), pa, la, va, pc, lc, vc))
        return sorted(options, key=lambda x: x[0])[:8]

    def nearby_nodes(pos, layer, blocked):
        gi, gj = grid(pos)
        result = []
        for di in range(-1, 2):
            for dj in range(-1, 2):
                node = gi+di, gj+dj
                if inside(node) and node not in blocked:
                    result.append((math.dist(pos, grid_xy(node)), node, layer))
        return sorted(result)

    debug_paths = 0

    def find_path(start_pos, start_layer, end_pos, end_layer, blocked, via_blocked,
                  via_retry=0):
        nonlocal debug_paths
        starts = nearby_nodes(start_pos, start_layer, blocked[start_layer])
        goals = {node: math.dist(end_pos, grid_xy(node))
                 for _, node, _ in nearby_nodes(end_pos, end_layer, blocked[end_layer])}
        if not starts or not goals:
            if debug_paths < 6:
                print(f"  no endpoint access {start_pos} -> {end_pos}: "
                      f"{len(starts)} starts, {len(goals)} goals", flush=True)
                debug_paths += 1
            return None
        if debug_paths < 6:
            print(f"  endpoints {start_layer}->{end_layer}: {len(starts)} starts "
                  f"{[x[1] for x in starts]}, {len(goals)} goals {list(goals)}",
                  flush=True)
        start_nodes = [(node, layer, cost) for cost, node, layer in starts]
        goal_states = {(node[0], node[1], end_layer) for node in goals}

        def heuristic(node, layer):
            distance = min(abs(node[0]-goal[0])+abs(node[1]-goal[1])
                           for goal in goals)
            return distance*10+(0 if layer == end_layer else 36)

        queue = []
        costs = {}
        came = {}
        for node, layer, attach_cost in start_nodes:
            state = (node[0], node[1], layer)
            cost = round(attach_cost*10)
            if cost < costs.get(state, 10**9):
                costs[state] = cost
                heapq.heappush(queue, (cost+heuristic(node, layer), cost, state))
        reached = None
        visited = 0
        while queue:
            _, spent, state = heapq.heappop(queue)
            if spent != costs.get(state):
                continue
            node = state[0], state[1]
            layer = state[2]
            if state in goal_states:
                reached = state
                break
            visited += 1
            if visited > 30000:
                break
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nxt = node[0]+di, node[1]+dj
                if not inside(nxt) or nxt in blocked[layer]:
                    continue
                next_state = (nxt[0], nxt[1], layer)
                new_cost = spent+10
                if new_cost < costs.get(next_state, 10**9):
                    costs[next_state] = new_cost
                    came[next_state] = state
                    heapq.heappush(queue, (new_cost+heuristic(nxt, layer),
                                           new_cost, next_state))
            if node not in via_blocked:
                other = pcb.B_Cu if layer == pcb.F_Cu else pcb.F_Cu
                next_state = (node[0], node[1], other)
                new_cost = spent+36
                if new_cost < costs.get(next_state, 10**9):
                    costs[next_state] = new_cost
                    came[next_state] = state
                    heapq.heappush(queue, (new_cost+heuristic(node, other),
                                           new_cost, next_state))
        if reached is None:
            if debug_paths < 6:
                print(f"  no path {start_pos} -> {end_pos}: visited {visited}, "
                      f"blocked={len(blocked[start_layer])}/{len(blocked[end_layer])}, "
                      f"vias={len(via_blocked)}", flush=True)
                debug_paths += 1
            return None
        path = [reached]
        while path[-1] in came:
            path.append(came[path[-1]])
        path.reverse()
        vias = [grid_xy(first[:2]) for first, second in zip(path, path[1:])
                if first[2] != second[2]]
        for i, first in enumerate(vias):
            for second in vias[i+1:]:
                if math.dist(first, second) < 0.56:
                    mark_disk(via_blocked, first, 0.56)
                    if via_retry < 10:
                        return find_path(start_pos, start_layer, end_pos, end_layer,
                                         blocked, via_blocked, via_retry+1)
                    return None
        if debug_paths < 6:
            print(f"  path found {start_pos} -> {end_pos}: visited {visited}, "
                  f"length {len(path)}", flush=True)
            debug_paths += 1
        return path

    def add_route(net, width, path, start_pos, end_pos, net_code,
                  start_via=False, end_via=False):
        added_vias = []

        def add_track(a, c, layer):
            if math.dist(a, c) < 0.001:
                return
            track = pcb.PCB_TRACK(b)
            track.SetStart(pcb.VECTOR2I(pcb.FromMM(a[0]), pcb.FromMM(a[1])))
            track.SetEnd(pcb.VECTOR2I(pcb.FromMM(c[0]), pcb.FromMM(c[1])))
            track.SetWidth(pcb.FromMM(width))
            track.SetLayer(layer)
            track.SetNetCode(net_code)
            b.Add(track)

        def add_via(pos):
            via = pcb.PCB_VIA(b)
            via.SetPosition(pcb.VECTOR2I(pcb.FromMM(pos[0]), pcb.FromMM(pos[1])))
            via.SetWidth(pcb.FromMM(0.5))
            via.SetDrill(pcb.FromMM(0.25))
            via.SetViaType(pcb.VIATYPE_THROUGH)
            via.SetLayerPair(pcb.F_Cu, pcb.B_Cu)
            via.SetNetCode(net_code)
            b.Add(via)
            added_vias.append(pos)

        if start_via:
            add_via(start_pos)
        add_track(start_pos, grid_xy(path[0][:2]), path[0][2])
        for first, second in zip(path, path[1:]):
            p1, p2 = grid_xy(first[:2]), grid_xy(second[:2])
            if first[2] != second[2]:
                add_via(p1)
            else:
                add_track(p1, p2, first[2])
        add_track(grid_xy(path[-1][:2]), end_pos, path[-1][2])
        if end_via:
            add_via(end_pos)
        return added_vias

    routed = 0
    deferred = []
    b.BuildConnectivity()
    net_names = sorted({item.GetNetname()
                        for fp in b.GetFootprints() for item in fp.Pads()
                        if item.GetNetCode()})
    priority = {"GPIO12_STRAP": 0, "NTC1_ADC": 1, "USB_VBUS": 2,
                "UART_DTR": 3, "UART_RTS": 4, "FAN4_TACH": 5,
                "INV1": 6, "UART_TX_5V": 7, "USB_D+": 8, "USB_D-": 8}
    net_names = sorted((name for name in net_names
                        if name not in ("+12V_FAN", "+12V_SATA")
                        and (include_ground or name != "GND")),
                       key=lambda name: (priority.get(name, 10), name))
    if include_ground:
        priority["GND"] = 20
    if only_nets is not None:
        net_names = [name for name in net_names if name in only_nets]
    for net in net_names:
        width = 0.4 if net in ("+3V3", "+5V_SYS", "+5V_SATA", "USB_VBUS") else 0.25
        net_code = b.FindNet(net).GetNetCode()
        blocked, via_blocked = obstacles(net, width)
        attempts = 0
        max_connections = 128 if net == "GND" else 16
        while attempts < max_connections:
            groups = components(net)
            if len(groups) <= 1:
                break
            options = []
            for i, left in enumerate(groups):
                for right in groups[i+1:]:
                    pairs = candidate_pairs(left, right)
                    if pairs:
                        options.append((pairs[0][0], left, right, pairs))
            options.sort(key=lambda x: x[0])
            connected = False
            pair_limit = len(options) if net == "GND" else 4
            anchor_limit = 24 if net == "GND" else 8
            for _, _, _, pairs in options[:pair_limit]:
                for _, start_pos, start_layer, start_via, end_pos, end_layer, end_via in pairs[:anchor_limit]:
                    path = find_path(start_pos, start_layer, end_pos, end_layer,
                                     blocked, via_blocked)
                    if path is not None:
                        added_vias = add_route(
                            net, width, path, start_pos, end_pos, net_code,
                            start_via, end_via)
                        for pos in added_vias:
                            mark_disk(via_blocked, pos, clearance+0.3+0.25)
                        b.BuildConnectivity()
                        routed += 1
                        connected = True
                        break
                if connected:
                    break
            if not connected:
                deferred.append((net, len(groups)))
                print(f"  deferred {net}: {len(groups)} disconnected groups", flush=True)
                break
            attempts += 1
            print(f"  routed {net} connection {attempts}/{len(groups)-1}",
                  flush=True)
        else:
            deferred.append((net, len(components(net))))
    print(f"Global routing: {routed} connections; deferred {deferred}")
    return routed, deferred


def zones(b):
    for layer in (pcb.B_Cu,):
        zone = pcb.ZONE(b)
        zone.SetLayer(layer)
        zone.SetNetCode(b.FindNet("GND").GetNetCode())
        zone.SetIslandRemovalMode(pcb.ISLAND_REMOVAL_MODE_ALWAYS)
        zone.SetLocalClearance(mm(0.25))
        zone.SetThermalReliefGap(mm(0.3))
        zone.SetThermalReliefSpokeWidth(mm(0.4))
        zone.SetPadConnection(pcb.ZONE_CONNECTION_FULL)
        poly = zone.Outline()
        poly.NewOutline()
        for x, y in ((100.5, 100.5), (173.5, 100.5),
                     (173.5, 153.5), (100.5, 153.5)):
            poly.Append(mm(x), mm(y))
        b.Add(zone)


def stitch_back_copper_islands(b):
    """Bridge nearby same-net back-pour islands around other-net copper."""
    step = 0.25
    origin = 100.0
    clearance = 0.21
    bounds = (4, 292, 4, 212)

    def grid(pos):
        return round((pos[0]-origin)/step), round((pos[1]-origin)/step)

    def coords(node):
        return origin+node[0]*step, origin+node[1]*step

    def inside(node):
        x, y = coords(node)
        return (bounds[0] <= node[0] <= bounds[1]
                and bounds[2] <= node[1] <= bounds[3]
                and not (119 <= x <= 167 and y <= 107.44))

    def obstacles(board):
        blocked = set()

        def mark_disk(pos, radius):
            ci, cj = grid(pos)
            cells = math.ceil((radius+step*0.08)/step)
            for di in range(-cells, cells+1):
                for dj in range(-cells, cells+1):
                    node = ci+di, cj+dj
                    if inside(node) and math.hypot(di, dj)*step <= radius+step*0.08:
                        blocked.add(node)

        pads = [pad for fp in board.GetFootprints() for pad in fp.Pads()
                if pad.GetNetname() and pad.GetNetname() != "GND"
                and pad.GetLayerSet().Contains(pcb.B_Cu)]
        for pad in pads:
            bb = pad.GetBoundingBox()
            margin = clearance+0.125
            x0, y0 = pcb.ToMM(bb.GetX())-margin, pcb.ToMM(bb.GetY())-margin
            x1, y1 = pcb.ToMM(bb.GetRight())+margin, pcb.ToMM(bb.GetBottom())+margin
            for i in range(max(bounds[0], math.floor((x0-origin)/step)),
                           min(bounds[1], math.ceil((x1-origin)/step))+1):
                for j in range(max(bounds[2], math.floor((y0-origin)/step)),
                               min(bounds[3], math.ceil((y1-origin)/step))+1):
                    x, y = coords((i, j))
                    if x0 <= x <= x1 and y0 <= y <= y1:
                        blocked.add((i, j))
        for track in board.GetTracks():
            if track.GetNetname() == "GND":
                continue
            if isinstance(track, pcb.PCB_VIA):
                pos = (pcb.ToMM(track.GetPosition().x), pcb.ToMM(track.GetPosition().y))
                mark_disk(pos, clearance+0.125+pcb.ToMM(track.GetWidth(pcb.B_Cu))/2)
            elif track.GetLayer() == pcb.B_Cu:
                start = (pcb.ToMM(track.GetStart().x), pcb.ToMM(track.GetStart().y))
                end = (pcb.ToMM(track.GetEnd().x), pcb.ToMM(track.GetEnd().y))
                radius = clearance+(0.25+pcb.ToMM(track.GetWidth()))/2
                count = max(1, math.ceil(math.dist(start, end)/(step/2)))
                for k in range(count+1):
                    t = k/count
                    mark_disk((start[0]+t*(end[0]-start[0]),
                               start[1]+t*(end[1]-start[1])), radius)
        return blocked

    def route(board, start, end, blocked):
        def nearby(pos):
            i, j = grid(pos)
            return [(node, math.dist(pos, coords(node)))
                    for di in range(-1, 2) for dj in range(-1, 2)
                    if inside(node := (i+di, j+dj)) and node not in blocked]

        starts, goals = nearby(start), nearby(end)
        if not starts or not goals:
            return None
        goal_set = {node for node, _ in goals}
        queue, cost, came = [], {}, {}
        for node, attach in starts:
            cost[node] = round(attach*10)
            distance = min(abs(node[0]-g[0])+abs(node[1]-g[1]) for g in goal_set)
            heapq.heappush(queue, (cost[node]+distance*10, cost[node], node))
        reached = None
        while queue:
            _, spent, cur = heapq.heappop(queue)
            if spent != cost.get(cur):
                continue
            if cur in goal_set:
                reached = cur
                break
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nxt = cur[0]+di, cur[1]+dj
                if not inside(nxt) or nxt in blocked:
                    continue
                new_cost = spent+10
                if new_cost >= cost.get(nxt, 10**9):
                    continue
                cost[nxt] = new_cost
                came[nxt] = cur
                distance = min(abs(nxt[0]-g[0])+abs(nxt[1]-g[1]) for g in goal_set)
                heapq.heappush(queue, (new_cost+distance*10, new_cost, nxt))
        if reached is None:
            return None
        path = [reached]
        while path[-1] in came:
            path.append(came[path[-1]])
        path.reverse()
        return path

    for zone in b.Zones():
        if zone.GetNetname() != "GND" or zone.GetLayer() != pcb.B_Cu:
            continue
        polygons = zone.GetFilledPolysList(pcb.B_Cu)
        main = polygons.COutline(0)
        main_points = [main.CPoint(i) for i in range(main.PointCount())]
        for index in range(1, polygons.OutlineCount()):
            island = polygons.COutline(index)
            island_points = [island.CPoint(i) for i in range(island.PointCount())]
            distance, start, end = min(
                (math.hypot(pcb.ToMM(left.x-right.x), pcb.ToMM(left.y-right.y)),
                 left, right)
                for left in island_points for right in main_points)
            if distance > 12:
                continue
            start_xy = (pcb.ToMM(start.x), pcb.ToMM(start.y))
            end_xy = (pcb.ToMM(end.x), pcb.ToMM(end.y))
            path = route(b, start_xy, end_xy, obstacles(b))
            if path is None:
                print(f"  no safe back-pour bridge {index}: {distance:.2f} mm",
                      flush=True)
                continue
            points = [start_xy]
            for i in range(1, len(path)-1):
                before = path[i][0]-path[i-1][0], path[i][1]-path[i-1][1]
                after = path[i+1][0]-path[i][0], path[i+1][1]-path[i][1]
                if before != after:
                    points.append(coords(path[i]))
            points.append(end_xy)
            for first, last in zip(points, points[1:]):
                track = pcb.PCB_TRACK(b)
                track.SetStart(point(*first))
                track.SetEnd(point(*last))
                track.SetWidth(mm(0.25))
                track.SetLayer(pcb.B_Cu)
                track.SetNetCode(b.FindNet("GND").GetNetCode())
                b.Add(track)
            print(f"  GND back-pour bridge {index}: {distance:.2f} mm", flush=True)


def power_and_ground(b):
    """12 V main copper on B.Cu; 1 mm branches to the four through-hole fans."""
    def xy(pad):
        return pcb.ToMM(pad.GetPosition().x), pcb.ToMM(pad.GetPosition().y)

    def pad(ref, number):
        return next(p for part in PARTS if part["ref"] == ref
                    for p in part["fp"].Pads() if p.GetNumber() == str(number))

    def trace(net, a, c, width, layer=pcb.B_Cu):
        item = pcb.PCB_TRACK(b)
        item.SetStart(point(*a))
        item.SetEnd(point(*c))
        item.SetWidth(mm(width))
        item.SetLayer(layer)
        item.SetNetCode(b.FindNet(net).GetNetCode())
        item.SetHasSolderMask(False)
        b.Add(item)

    def via(net, pos, diameter=1.0, drill=0.5):
        item = pcb.PCB_VIA(b)
        item.SetPosition(point(*pos))
        item.SetWidth(mm(diameter))
        item.SetDrill(mm(drill))
        item.SetViaType(pcb.VIATYPE_THROUGH)
        item.SetLayerPair(pcb.F_Cu, pcb.B_Cu)
        item.SetNetCode(b.FindNet(net).GetNetCode())
        b.Add(item)

    sata_points = [xy(pad("J1", n)) for n in (13, 14, 15)]
    for x, y in sata_points:
        trace("+12V_SATA", (x, y), (111, y), 1.0)
    trace("+12V_SATA", (111, min(p[1] for p in sata_points)),
          (111, max(p[1] for p in sata_points)), 1.0)
    middle_y = sata_points[1][1]
    trace("+12V_SATA", (111, middle_y), (109, middle_y), 2.0)
    trace("+12V_SATA", (109, middle_y), (109, 141), 2.0)
    trace("+12V_SATA", (109, 141), (114.5, 141), 2.0)
    via("+12V_SATA", (114.5, 141))
    trace("+12V_SATA", (114.5, 141), xy(pad("F1", 1)), 1.0, pcb.F_Cu)
    via("+12V_FAN", (120, 142))
    trace("+12V_FAN", xy(pad("F1", 2)), (120, 142), 1.0, pcb.F_Cu)
    trace("+12V_FAN", (120, 142), (120, 144), 2.0)
    cx, cy = xy(pad("C1", 1))
    trace("+12V_FAN", (114.54, 144), (cx, 144), 2.0)
    via("+12V_FAN", (cx, cy), 0.8, 0.4)
    trace("+12V_FAN", (cx, cy), (cx, 144), 1.0)
    for ref in ("J3", "J4", "J5", "J6"):
        x, y = xy(pad(ref, 2))
        trace("+12V_FAN", (x, 144), (x, y), 1.0)
    via("+12V_FAN", (155, 142))
    trace("+12V_FAN", (155, 142), (155, 144), 1.0)
    trace("+12V_FAN", (155, 142), xy(pad("D3", 1)), 1.0, pcb.F_Cu)
    # SMD ground pads connect to the back plane with tented vias. For this
    # hand-built WIP these are via-in-pad, NOT an assembly-ready specification.
    ground_vias = 0
    seen = set()
    back_power = [(t.GetStart(), t.GetEnd(), pcb.ToMM(t.GetWidth()))
                  for t in b.GetTracks() if not isinstance(t, pcb.PCB_VIA)
                  and t.GetLayer() == pcb.B_Cu]
    def near_power(pos):
        for a, c, width in back_power:
            ax, ay, cx, cy = pcb.ToMM(a.x), pcb.ToMM(a.y), pcb.ToMM(c.x), pcb.ToMM(c.y)
            length2 = (cx-ax)**2+(cy-ay)**2
            t = max(0, min(1, ((pos[0]-ax)*(cx-ax)+(pos[1]-ay)*(cy-ay))/length2)) if length2 else 0
            if math.dist(pos, (ax+t*(cx-ax), ay+t*(cy-ay))) < width/2+0.5:
                return True
        return False
    for part in PARTS:
        for item in part["fp"].Pads():
            if item.GetNetname() == "GND" and item.GetAttribute() == pcb.PAD_ATTRIB_SMD:
                pos = xy(item)
                if pos in seen or near_power(pos):
                    continue
                seen.add(pos)
                via("GND", pos, 0.5, 0.25)
                ground_vias += 1
    print(f"12 V 2mm back-layer main / 1mm fan branches; {ground_vias} ground vias.")


def project():
    data = {
        "meta": {"filename": f"{NAME}.kicad_pro", "version": 1},
        "board": {"design_settings": {
            "rules": {"min_clearance": 0.2, "min_track_width": 0.2,
                      "min_via_diameter": 0.5, "min_through_hole_diameter": 0.2,
                      "min_hole_clearance": 0.15, "min_silk_text_height": 0.7,
                      "min_via_annular_width": 0.125, "min_copper_edge_clearance": 0.3},
            "rule_severities": {},
            "track_widths": [0.25, 0.4, 1.0, 2.0],
            "via_dimensions": [{"diameter": 0.6, "drill": 0.3}],
        }},
        "net_settings": {"classes": [
            {"name": "Default", "clearance": 0.2, "track_width": 0.25,
             "via_diameter": 0.6, "via_drill": 0.3, "microvia_diameter": 0.3,
             "microvia_drill": 0.1, "diff_pair_width": 0.25, "diff_pair_gap": 0.2,
             "diff_pair_via_gap": 0.25},
            {"name": "Power", "clearance": 0.2, "track_width": 0.4,
             "via_diameter": 0.8, "via_drill": 0.4},
            {"name": "Fan12V", "clearance": 0.2, "track_width": 2.0,
             "via_diameter": 1.0, "via_drill": 0.5},
        ], "netclass_patterns": [
            {"netclass": "Fan12V", "pattern": "+12V*"},
            {"netclass": "Power", "pattern": "+5V*"},
            {"netclass": "Power", "pattern": "+3V3"},
            {"netclass": "Power", "pattern": "USB_VBUS"},
        ], "meta": {"version": 4}},
    }
    (HERE / f"{NAME}.kicad_pro").write_text(json.dumps(data, indent=2)+"\n", encoding="utf-8")
    (HERE / f"{NAME}.kicad_dru").write_text(
        '(version 1)\n'
        '(rule "12V main route 2mm minimum" (condition "A.Type == \'Track\' && '
        '(A.NetName == \'+12V_FAN\' || A.NetName == \'+12V_SATA\')")'
        ' (constraint track_width (min 1.0) (opt 2.0)))\n', encoding="utf-8")


def exports():
    rows = [{k: p[k] for k in ("ref", "value", "footprint", "source")} for p in PARTS]
    with (HERE / "prototype-bom.csv").open("w", newline="", encoding="utf-8") as out:
        writer = csv.DictWriter(out, fieldnames=("ref", "value", "footprint", "source"))
        writer.writeheader()
        writer.writerows(rows)
    manifest_json = [{k: v for k, v in p.items() if k != "fp"} for p in PARTS]
    (HERE / "connectivity.json").write_text(json.dumps(
        {"status": "PROTOTYPE WIP - NOT FOR FABRICATION", "parts": manifest_json},
        indent=2)+"\n", encoding="utf-8")


def exported_connectivity():
    """Use KiCad's own netlist, including its intentionally unconnected nets."""
    subprocess.run([str(SHARE.parents[1] / "bin" / "kicad-cli.exe"),
                    "sch", "export", "netlist", str(HERE / f"{NAME}.kicad_sch"),
                    "--output", str(HERE / f"{NAME}.net")], check=True)
    tree = parse((HERE / f"{NAME}.net").read_text(encoding="utf-8"))
    actual = {}
    for net in children(child(tree, "nets"), "net"):
        name = value(child(net, "name")[1])
        for node in children(net, "node"):
            ref = value(child(node, "ref")[1])
            pin = value(child(node, "pin")[1])
            actual.setdefault(ref, {})[pin] = name
    for part in PARTS:
        pins = actual.get(part["ref"], {})
        for number, expected in part["nets"].items():
            found = pins.get(number)
            if expected and found != expected:
                raise RuntimeError(f'{part["ref"]}.{number}: {found!r}, expected {expected!r}')
            if expected is None and found and not found.startswith("unconnected-"):
                raise RuntimeError(f'{part["ref"]}.{number}: intentional NC became {found}')
        part["pad_nets"] = pins
    print("KiCad exported netlist matches every connected manifest pin.")


def main():
    manifest()
    renamed_symbols()
    sata_footprint()
    schematic()
    project()
    exported_connectivity()
    b = board()
    import routing
    routing.restore(b)
    zones(b)
    pcb.ZONE_FILLER(b).Fill(b.Zones())
    pcb.SaveBoard(str(HERE / f"{NAME}.kicad_pcb"), b)
    exports()
    print(f"Saved {len(PARTS)} electrical footprints + 4 M2 holes, "
          f"{b.GetNetCount()-1} nets, 74 x 54 mm, 2 layers.")
    print("NOT FOR FABRICATION: routing complete; SATA mechanics provisional.")


if __name__ == "__main__":
    main()
