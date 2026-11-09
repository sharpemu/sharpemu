<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Aerolib Catalog

```bash
# NID to export name
python scripts/aerolib_catalog.py lookup Zxa0VhQVTsk

# Export name to NID
python scripts/aerolib_catalog.py lookup sceKernelWaitSema

# A comma can be used for multiple query strings.
python scripts/aerolib_catalog.py lookup Zxa0VhQVTsk,SaKib2Ug0yI,sceVideoOutOpen,amuBfI-AQc4

# Search export names
python scripts/aerolib_catalog.py search VideoOut --limit 20

# Export all NID/name pairs to artifacts/aerolib.txt
python scripts/aerolib_catalog.py export
```
