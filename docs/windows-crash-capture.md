<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Windows crash capture

In the launcher, open **Options > Logging** and enable **Crash dumps** before
you start a game. The same toggle is available in each game's Logging settings.
It sets `SHARPEMU_CRASH_CAPTURE` and is off by default.

For a diagnostic run on Windows x64, you can also set it from PowerShell:

```powershell
$env:SHARPEMU_CRASH_CAPTURE = '1'
.\SharpEmu.exe
```

Start a new launcher from that PowerShell window. The game process starts a
hidden helper before runtime initialization. No SDK or debugger install is
required. Look for `[CRASH][INFO] Crash capture ready` in the game log.

The helper writes a full dump beside the run log. Without a log path, it uses
`user/logs` beside the executable. Names include the game process ID:

- `<run>.crash-<pid>.dmp`: the memory dump.
- `<run>.crash-<pid>.dmp.capture.log`: capture status and errors.

Full dumps can use many GB of disk space and contain private process data.
Keep them local until you choose to share them. There is no automatic upload.

The helper uses Windows debugger events. It passes first-chance exceptions to
the application and captures only second-chance, unhandled exceptions. Normal
handled page faults do not create dumps. A normal exit or a forced process
termination does not create a dump either. The original crash is not suppressed.

This option changes debugger presence and can change timing. Do not use it for
FPS comparisons. Do not attach WinDbg, ProcDump, or another native debugger at
the same time. Capture failures are recorded when possible; no capture method
can cover every process termination. A failed write can leave an incomplete dump.

This is separate from the `DOTNET_Dbg*` settings. It does not change them.
To disable a GUI selection, turn off **Crash dumps** before the next game launch.
If you set the option in PowerShell, close the launcher and run:

```powershell
Remove-Item Env:SHARPEMU_CRASH_CAPTURE -ErrorAction SilentlyContinue
```
