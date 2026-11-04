"""Check actual KiCad netlist/PCB connectivity and retain honest WIP counts."""
import collections
import json
from pathlib import Path
import re

import pcbnew as pcb

import generate as g

HERE = Path(__file__).resolve().parent


def main():
    data = json.loads((HERE / "connectivity.json").read_text(encoding="utf-8"))
    parts = {p["ref"]: p for p in data["parts"]}
    b = pcb.LoadBoard(str(HERE / "FanController.kicad_pcb"))
    footprints = {f.GetReference(): f for f in b.GetFootprints()}
    checked = 0
    for ref, part in parts.items():
        fp = footprints[ref]
        assert fp.GetValue() == part["value"], ref
        for pad in fp.Pads():
            number = pad.GetNumber()
            if number:
                assert pad.GetNetname() == part["pad_nets"].get(number, ""), (ref, number)
                checked += 1
    module = parts["U1"]["nets"]
    pins = {"FAN1_GPIO_PWM": "23", "FAN2_GPIO_PWM": "27", "FAN3_GPIO_PWM": "28",
            "FAN4_GPIO_PWM": "30", "FAN1_TACH": "16", "FAN2_TACH": "6",
            "FAN3_TACH": "13", "FAN4_TACH": "12", "NTC1_ADC": "9", "NTC2_ADC": "8"}
    for net, pin in pins.items():
        assert module[pin] == net, (net, pin)
    firmware = (HERE.parents[1] / "esp32" / "include" / "HardwareConfig.h").read_text()
    for name, expected in (("PwmPins", [15, 16, 17, 18]),
                           ("TachPins", [13, 34, 14, 27]), ("SensorPins", [33, 32])):
        match = re.search(rf"{name}\[[^\]]*\]\s*=\s*\{{([^}}]+)\}}", firmware)
        assert [int(x) for x in match[1].split(",")] == expected, name
    assert "PwmInverted = false" in firmware
    assert parts["J1"]["nets"]["11"] is None
    assert parts["J1"]["nets"]["7"] == "+5V_SATA"
    for ref in ("D1", "D2"):
        assert parts[ref]["nets"]["1"] == "+5V_SYS"
        assert parts[ref]["nets"]["2"] != "+5V_SYS"
    assert parts["D3"]["nets"] == {"1": "+12V_FAN", "2": "GND"}
    assert parts["U2"]["nets"] == {"1": "GND", "2": "+3V3", "3": "+5V_SYS"}
    assert parts["U5"]["nets"]["14"] == "+3V3"
    for n in range(1, 5):
        assert parts[f"J{n+2}"]["nets"] == {
            "1": "GND", "2": "+12V_FAN", "3": f"FAN{n}_TACH", "4": f"FAN{n}_PWM"}
        assert parts[f"R{14+n}"]["nets"] == {"1": f"INV{n}", "2": f"BASE{n}"}
        assert parts[f"Q{n}"]["nets"] == {"1": f"BASE{n}", "2": "GND", "3": f"FAN{n}_PWM"}
    custom = g.parse((HERE / "FanController.pretty" / "SATA_Power_B_Provisional.kicad_mod").read_text())
    pads = g.children(custom, "pad")
    contacts = [p for p in pads if g.value(p[1])]
    assert len(contacts) == 15
    for i, p in enumerate(contacts):
        assert abs(float(g.child(p, "at")[1])-i*1.27) < 0.00001
        assert float(g.child(p, "drill")[1]) == 0.8
    locator = next(p for p in pads if not g.value(p[1]))
    assert len(pads)-len(contacts) == 1
    assert [float(x) for x in g.child(locator, "at")[1:]] == [-4.91, 7.92]
    assert float(g.child(locator, "drill")[1]) == 1.2
    assert b.GetCopperLayerCount() == 2
    bb = b.GetBoardEdgesBoundingBox()
    assert abs(pcb.ToMM(bb.GetWidth())-74) < 0.1
    assert abs(pcb.ToMM(bb.GetHeight())-54) < 0.1
    for n, xy in enumerate(((104, 104), (170, 104), (104, 150), (170, 150)), 1):
        pos = footprints[f"H{n}"].GetPosition()
        assert (pcb.ToMM(pos.x), pcb.ToMM(pos.y)) == xy
    erc = json.loads((HERE / "erc.json").read_text(encoding="utf-8"))
    ev = [v for s in erc["sheets"] for v in s["violations"]]
    assert not ev, "Schematic ERC is not clean"
    drc = json.loads((HERE / "drc.json").read_text(encoding="utf-8"))
    assert not drc["schematic_parity"], "Schematic and PCB disagree"
    errors = [v for v in drc["violations"] if v["severity"] == "error"]
    assert not errors, f"PCB physical errors: {errors}"
    unconnected = drc["unconnected_items"]
    assert not unconnected, "PCB routing is incomplete"
    assert not drc["violations"], "PCB still has DRC warnings"
    import routing
    copper = json.loads(routing.SOURCE.read_text(encoding="utf-8"))
    assert routing.pad_geometry(b) == copper["pads"], "Copper source pad mismatch"
    actual_tracks, actual_vias = routing.copper_geometry(b)
    assert actual_tracks == copper["tracks"], "PCB tracks differ from routing.json"
    assert actual_vias == copper["vias"], "PCB vias differ from routing.json"
    unconnected_details = [
        item for violation in unconnected for item in violation["items"]
    ]
    tracks = list(b.GetTracks())
    via_count = sum(isinstance(t, pcb.PCB_VIA) for t in tracks)
    summary = {
        "status": "WIP - NOT FOR FABRICATION",
        "kicad_version": pcb.Version(),
        "electrical_components": len(parts),
        "mounting_holes": 4,
        "checked_physical_pads": checked,
        "functional_nets": len({n for p in parts.values() for n in p["nets"].values() if n}),
        "pcb_nets_including_intentional_nc": b.GetNetCount()-1,
        "track_segments": len(tracks)-via_count,
        "vias": via_count,
        "erc_errors": 0,
        "erc_warnings": 0,
        "drc_physical_errors": 0,
        "drc_warnings": len(drc["violations"]),
        "drc_warning_types": dict(collections.Counter(v["type"] for v in drc["violations"])),
        "schematic_parity_issues": 0,
        "unconnected_items": len(unconnected),
        "unconnected_details": unconnected_details,
        "manufacturing_blockers": [
            "SATA electrical P1 orientation and opposite locating hole unverified",
            "Connector and fuse current/I2t qualifications outstanding",
            "USB current before enumeration, UART margins and powered-off host behavior untested",
            "LDO thermal copper/dissipation and 3A fan startup untested",
            "Via-in-pad is a hand-built prototype assumption, not an assembly specification",
        ],
    }
    for pattern in ("*.gbr", "*.drl", "*.zip"):
        assert not list(HERE.rglob(pattern)), f"Unexpected fabrication package: {pattern}"
    (HERE / "validation-summary.json").write_text(json.dumps(summary, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
