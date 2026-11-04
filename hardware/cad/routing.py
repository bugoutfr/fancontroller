"""Persist validated copper without rerunning the exploratory autorouter.

Coordinates and widths are integer KiCad internal units. The pad geometry guard
rejects replay after placement, net assignment or footprint-library changes.
"""
import json
from pathlib import Path

import pcbnew as pcb

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "routing.json"


def vector(pos):
    return [pos.x, pos.y]


def pad_geometry(board):
    pads = []
    for footprint in board.GetFootprints():
        for pad in footprint.Pads():
            pads.append({
                "ref": footprint.GetReference(),
                "pad": pad.GetNumber(),
                "net": pad.GetNetname(),
                "position": vector(pad.GetPosition()),
                "size": vector(pad.GetSize()),
                "drill": vector(pad.GetDrillSize()),
                "orientation": pad.GetOrientationDegrees(),
                "layers": list(pad.GetLayerSet().Seq()),
                "shape": int(pad.GetShape()),
            })
    return sorted(pads, key=lambda x: (x["ref"], x["pad"], x["position"]))


def copper_geometry(board):
    tracks, vias = [], []
    for item in board.GetTracks():
        data = {"net": item.GetNetname(), "uuid": item.m_Uuid.AsString()}
        if isinstance(item, pcb.PCB_VIA):
            data.update(position=vector(item.GetPosition()),
                        diameter=item.GetWidth(pcb.F_Cu), drill=item.GetDrillValue(),
                        layers=[item.TopLayer(), item.BottomLayer()],
                        tenting=[int(item.GetFrontTentingMode()),
                                 int(item.GetBackTentingMode())])
            vias.append(data)
        else:
            data.update(start=vector(item.GetStart()), end=vector(item.GetEnd()),
                        width=item.GetWidth(), layer=item.GetLayer(),
                        solder_mask=item.HasSolderMask())
            tracks.append(data)
    return (sorted(tracks, key=lambda x: x["uuid"]),
            sorted(vias, key=lambda x: x["uuid"]))


def capture(board):
    drc = json.loads((HERE / "drc.json").read_text(encoding="utf-8"))
    assert not any(drc[k] for k in ("violations", "unconnected_items", "schematic_parity")), (
        "Capture requires a completely clean DRC report from this board")
    tracks, vias = copper_geometry(board)
    data = {"version": 1, "units": "KiCad internal units (1000000 per mm)",
            "pads": pad_geometry(board),
            "tracks": tracks, "vias": vias}
    SOURCE.write_text(json.dumps(data, indent=2)+"\n", encoding="utf-8")
    print(f"Captured validated copper: {len(tracks)} segments, {len(vias)} vias.")


def restore(board):
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    if data["version"] != 1 or pad_geometry(board) != data["pads"]:
        raise RuntimeError("routing.json does not match the generated pads; "
                           "do not replay copper after a placement/net/footprint change")
    if list(board.GetTracks()):
        raise RuntimeError("Restore requires a board without tracks or vias")
    for entry in data["tracks"]:
        item = pcb.PCB_TRACK(board)
        item.SetStart(pcb.VECTOR2I(*entry["start"]))
        item.SetEnd(pcb.VECTOR2I(*entry["end"]))
        item.SetWidth(entry["width"])
        item.SetLayer(entry["layer"])
        item.SetHasSolderMask(entry["solder_mask"])
        item.SetNetCode(board.FindNet(entry["net"]).GetNetCode())
        item.SetUuid(pcb.KIID(entry["uuid"]))
        board.Add(item)
    for entry in data["vias"]:
        item = pcb.PCB_VIA(board)
        item.SetPosition(pcb.VECTOR2I(*entry["position"]))
        item.SetWidth(entry["diameter"])
        item.SetDrill(entry["drill"])
        item.SetViaType(pcb.VIATYPE_THROUGH)
        item.SetLayerPair(*entry["layers"])
        item.SetFrontTentingMode(entry["tenting"][0])
        item.SetBackTentingMode(entry["tenting"][1])
        item.SetNetCode(board.FindNet(entry["net"]).GetNetCode())
        item.SetUuid(pcb.KIID(entry["uuid"]))
        board.Add(item)
    print(f"Restored validated copper: {len(data['tracks'])} segments, "
          f"{len(data['vias'])} vias; no global rerouting.")


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", action="store_true", required=True,
                        help="After DRC on the edited PCB, update the copper source")
    parser.parse_args()
    capture(pcb.LoadBoard(str(HERE / "FanController.kicad_pcb")))
