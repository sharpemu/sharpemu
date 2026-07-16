# Debugging Guide

## Quick Start

### Run a game in headless mode (no GPU needed)
```bash
SHARPEMU_HEADLESS=1 dotnet artifacts/bin/Release/net10.0/linux-x64/SharpEmu.dll eboot.bin
```

### Run with Xvfb (virtual display)
```bash
Xvfb :99 -screen 0 1920x1080x24 &
export DISPLAY=:99
export XDG_RUNTIME_DIR=/tmp/runtime-dir
mkdir -p /tmp/runtime-dir && chmod 700 /tmp/runtime-dir
dotnet artifacts/bin/Release/net10.0/linux-x64/SharpEmu.dll eboot.bin
```

### Run with full GPU (needs Vulkan drivers)
```bash
dotnet artifacts/bin/Release/net10.0/linux-x64/SharpEmu.dll eboot.bin
```

## Reading Crash Reports

After a crash, check these files:

### 1. Crash Snapshot (auto-generated)
```
/home/z/my-project/download/crashes/crash_snapshot.txt
```

Contains:
- Fault address, RIP, registers
- First failure (root cause candidate)
- Last 30 imports before crash
- Missing NID report
- API state violations
- Pointer origin tracker

### 2. AI Debug Report (structured JSON)
```
data/ai-report-<gameid>.json
```

Contains structured data for AI analysis:
- Boot status per phase
- Import statistics
- Missing NIDs with severity
- Crash details with root cause candidate
- Timeline of events

### 3. Missing NID Database
```
data/missing-nids.json
```

Tracks all unresolved NIDs across all games.

## Common Crash Patterns

### NULL pointer dereference
```
RIP: 0x80000BFE
Instruction: mov rax, [rsi+0x18]
RSI: 0x0000000000000000
```
**Cause**: A game object was never initialized. Check which HLE call should have set the register.

### GPU address access
```
Fault: 0x1FE000000
Region: GPU memory (hardcoded)
```
**Cause**: Game writes to hardcoded GPU address. Ensure GPU placeholder is mapped.

### Host pointer leak
```
RDI: 0x7F95D4BD4F38
Region: HOST POINTER LEAK
```
**Cause**: libc malloc returns host addresses. HLE functions need host-pointer fallback.

## Diagnostics Environment Variables

| Variable | Purpose |
|----------|---------|
| `SHARPEMU_HEADLESS=1` | No GPU/Vulkan |
| `SHARPEMU_DUMP_FRAMES=1` | Log frame info |
| `SHARPEMU_LOG_DIRECT_MEMORY=1` | Trace memory allocations |
| `SHARPEMU_LOG_POSIX_SIGNALS=1` | Trace signals |
| `SHARPEMU_LOG_PTHREADS=1` | Trace pthread calls |
| `SHARPEMU_LOG_GUARDS=1` | Trace C++ guard variables |

## Testing a New Game

1. Extract eboot.bin from the game
2. Run: `SHARPEMU_HEADLESS=1 SharpEmu eboot.bin`
3. Check `crash_snapshot.txt` for crash details
4. Check `missing-nids.json` for missing HLE exports
5. Create a game profile in `games/PPSAxxxxx/profile.json`
6. Add the game to `data/game-database.json`

## Regression Testing

Before changing HLE code:
1. Run both test games (PPSA06328, PPSA14677)
2. Record import counts and crash locations
3. After changes, re-run both games
4. Verify import counts didn't decrease
5. Verify crash location didn't regress
