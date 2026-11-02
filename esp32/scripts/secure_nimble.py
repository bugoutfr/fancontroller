Import("env")

from pathlib import Path
import re

# NimBLE 2.5.2 otherwise deletes existing keys on repeat pairing.
# Refuse replacement: the owner must explicitly revoke with the physical button.
source = Path(env.subst("$PROJECT_LIBDEPS_DIR")) / env.subst("$PIOENV") / "NimBLE-Arduino" / "src" / "NimBLEServer.cpp"
text = source.read_text(encoding="utf-8")
marker = "// FanController: preserve bonds; physical revocation required."
if marker not in text:
    pattern = r"case BLE_GAP_EVENT_REPEAT_PAIRING: \{.*?\} // BLE_GAP_EVENT_REPEAT_PAIRING"
    replacement = (
        "case BLE_GAP_EVENT_REPEAT_PAIRING: {\n"
        "            " + marker + "\n"
        "            return BLE_GAP_REPEAT_PAIRING_IGNORE;\n"
        "        } // BLE_GAP_EVENT_REPEAT_PAIRING"
    )
    text, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError("NimBLE repeat-pairing guard not found; refusing insecure build")
    source.write_text(text, encoding="utf-8")
if "return BLE_GAP_REPEAT_PAIRING_RETRY;" in text:
    raise RuntimeError("Unexpected NimBLE repeat-pairing retry; review dependency before building")
