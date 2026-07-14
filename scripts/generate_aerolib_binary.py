# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

#!/usr/bin/env python3

import hashlib
import struct
from base64 import b64encode as base64enc
from binascii import unhexlify as uhx
from pathlib import Path

NAMES = 'scripts/ps5_names.txt'
OUTPUT = 'src/SharpEmu.HLE/Aerolib/aerolib.bin'

def name2nid(name):
    symbol = hashlib.sha1(name.encode() + uhx('518D64A635DED8C1E6B039B1C3E55230')).digest()
    id_val = struct.unpack('<Q', symbol[:8])[0]
    nid = base64enc(uhx('%016x' % id_val), b'+-').rstrip(b'=')
    return nid.decode('utf-8')

def generate():
    names_path = Path(NAMES)
    output_path = Path(OUTPUT)

    entries = []
    with open(names_path, 'r', encoding='utf-8') as f:
        for line in f:
            name = line.strip()
            if name:
                nid = name2nid(name)
                entries.append((nid, name))

    print(f"Found {len(entries)} entries")

    data = bytearray()
    data.extend(struct.pack('<I', len(entries)))

    for nid, name in entries:
        nid_bytes = nid.encode('utf-8')
        name_bytes = name.encode('utf-8')
        data.append(len(nid_bytes))
        data.extend(nid_bytes)
        data.extend(struct.pack('<H', len(name_bytes)))
        data.extend(name_bytes)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, 'wb') as f:
        f.write(data)

    print(f"Generated: {output_path} ({len(data):,} bytes)")
    print(f"Total entries: {len(entries)}")

if __name__ == "__main__":
    generate()
