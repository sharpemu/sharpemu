# SharpEmu Architecture

## Overview

SharpEmu is a PS5 emulator with a focus on **debugging infrastructure** and **AI-assisted development**.

## Project Structure

```
sharpemu/
├── src/
│   ├── SharpEmu.Core/          # CPU, Memory, Loader, Runtime
│   ├── SharpEmu.HLE/           # HLE framework, CpuContext, ModuleManager
│   ├── SharpEmu.Libs/          # HLE exports (Kernel, libc, AGC, VideoOut, etc.)
│   ├── SharpEmu.Diagnostics/   # Debug framework (standalone, no Core deps)
│   ├── SharpEmu.Logging/       # Logging infrastructure
│   ├── SharpEmu.CLI/           # Command-line interface
│   ├── SharpEmu.GUI/           # Desktop GUI (Avalonia)
│   └── SharpEmu.ShaderCompiler/ # GPU shader translation
├── data/
│   ├── missing-nids.json       # Persistent NID database (cross-game)
│   ├── game-database.json      # Compatibility database
│   └── ai-report-*.json        # AI debug reports per game
├── games/                      # Game profiles (NO game files!)
│   ├── PPSA06328/
│   └── PPSA14677/
├── docs/
│   ├── ARCHITECTURE.md         # This file
│   ├── DEBUGGING.md            # How to debug games
│   └── ADDING_GAME.md          # How to add a new game
└── .github/workflows/
    └── build-release.yml       # CI/CD for Windows + Linux builds
```

## Design Principles

1. **No game-specific hacks in core** — all game workarounds go in game profiles
2. **Diagnostics is standalone** — SharpEmu.Diagnostics has no dependency on Core
3. **AI-friendly output** — all crash reports are structured JSON
4. **Cross-platform** — Windows and Linux supported from day one
5. **No copyrighted files** — only metadata, NIDs, and debug logs in the repo

## Debug Infrastructure

### Diagnostics Components

| Component | Purpose |
|-----------|---------|
| PhaseEngine | Tracks boot phases (Loader → TLS → CRT → GPU → VideoOut) |
| BootGraph | Renders phase tree as text/JSON |
| ImportTimeline | Records every HLE import dispatch |
| ReturnAnalyzer | First Failure Detector + loop detector |
| MissingNidReporter | Final summary of all unresolved NIDs |
| PointerOriginTracker | Traces where pointer values come from |
| MemoryRegionClassifier | Classifies addresses (code/data/heap/gpu/hostleak) |
| ApiStateValidator | State machine for AGC/VideoOut/GNM |
| CrashPackage | Full crash dump written from signal handler |

### Environment Variables

| Variable | Effect |
|----------|--------|
| `SHARPEMU_HEADLESS=1` | Skip Vulkan/GPU, use headless VideoOut |
| `SHARPEMU_DUMP_FRAMES=1` | Log frame info in headless mode |
| `SHARPEMU_LOG_DIRECT_MEMORY=1` | Trace DirectMemory allocations |
| `SHARPEMU_LOG_POSIX_SIGNALS=1` | Trace every signal delivery |

## Game Compatibility

### PPSA06328 (Arise: A Simple Story)
- **Boot**: ✓ (824 imports)
- **AGC**: ✓ (52 shaders, 60 resources)
- **VideoOut**: ✓ (opened in headless)
- **Crash**: NULL deref at 0x80000BFE (game code issue)
- **Rating**: ⭐⭐⭐⭐⭐ Boot, ⭐⭐ Graphics

### PPSA14677 (Unknown 3D game)
- **Boot**: ✓ (611 imports)
- **AGC**: ✗ (not reached)
- **VideoOut**: ✗ (not reached)
- **Crash**: NULL deref at 0x8007F33FE (missing std::call_once)
- **Rating**: ⭐⭐⭐⭐ Boot, ⭐ Graphics

## AI Debug Interface

Each game run produces a structured JSON report at `data/ai-report-<gameid>.json`:

```json
{
  "crash": {
    "type": "null_pointer_dereference",
    "rip": "0x8007F33FE",
    "null_register": "RDI",
    "root_cause_candidate": {
      "suggested_fix": "Implement std::call_once",
      "fix_difficulty": "easy"
    }
  }
}
```

This allows any AI (ChatGPT, Claude, Gemini, Cursor) to analyze crashes without reading source code.
