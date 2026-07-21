# SharpEmu Diagnostics — Architecture

## Overview

SharpEmu.Diagnostics is a **plugin-based diagnostic framework** for the SharpEmu
PlayStation 5 emulator. It provides structured event collection, analysis, and
export without modifying emulator core logic.

## Design Principles

1. **Zero coupling** — Core/Libs/HLE reference only Contracts (interfaces)
2. **Plugin isolation** — Plugins communicate only through EventBus
3. **No IO in plugins** — Plugins return data; Exporter writes files
4. **Optional** — If Diagnostics is deleted, emulator builds and runs normally
5. **No game-specific code** — All plugins are general-purpose tools
6. **No AI/database** — Framework collects data; external tools consume it

## Architecture Diagram

```
SharpEmu.Diagnostics.Contracts (interfaces only)
    ↑ referenced by Core/Libs/HLE
    │
    │ DiagnosticAdapter (static bridge, zero overhead when inactive)
    │
SharpEmu.Diagnostics (implementation)
    ├── Core/
    │   ├── EventBus          — thread-safe event dispatcher
    │   ├── PluginRegistry    — Register<T>() dynamic registration
    │   ├── DiagnosticContext — session context
    │   ├── DiagnosticClock   — single time source
    │   └── EventFilter       — category-based filtering
    ├── Export/
    │   └── DiagnosticExporter — JSON, Text, Markdown output
    ├── Util/
    │   └── RingBuffer<T>     — generic lock-free ring buffer
    ├── DiagnosticConfig      — env vars + diagnostics.json
    ├── DiagnosticManager     — Start/Stop/Publish/Flush
    └── Plugins/
        ├── BootTimelinePlugin
        ├── ImportTimelinePlugin
        ├── FirstFailurePlugin
        ├── CpuTracePlugin
        ├── CrashPackagePlugin
        ├── ThreadTimelinePlugin
        ├── MemoryTimelinePlugin
        ├── StatisticsPlugin
        └── ConsoleSinkPlugin
```

## Event Flow

```
Emulator Core publishes event
         ↓
DiagnosticAdapter → DiagnosticManager.Publish()
         ↓
EventFilter (category filter)
         ↓
EventBus → dispatches to ALL registered plugins
         ↓
Each plugin's OnEvent() processes the event
         ↓
At shutdown: plugin.Shutdown() returns collected data
         ↓
DiagnosticExporter writes JSON + Text + Markdown
```

## Plugin Lifecycle

1. `DiagnosticManager.Start()` creates EventBus and PluginRegistry
2. Each enabled plugin is registered via `Register<T>()`
3. `plugin.Initialize(context)` is called — plugin saves context
4. Events flow through `plugin.OnEvent(event)` — plugin collects data
5. `plugin.Shutdown()` is called — plugin returns collected data
6. `DiagnosticExporter` writes data to JSON/Text/Markdown files

## Configuration

### Environment Variables

```bash
# Enable specific plugins
export SHARPEMU_DIAG_BOOT=1
export SHARPEMU_DIAG_IMPORTS=1
export SHARPEMU_DIAG_CRASH=1

# Enable all default plugins
export SHARPEMU_DIAG=1

# Filter events (only CPU and crash events)
export SHARPEMU_DIAG_FILTER=cpu,crash

# Live console output
export SHARPEMU_DIAG_CONSOLE=1
export SHARPEMU_DIAG_CONSOLE_FILTER=import,crash
```

### diagnostics.json

```json
{
    "BootTimeline": true,
    "ImportTimeline": true,
    "CpuTrace": false,
    "Statistics": true
}
```

## API Versioning

- `IDiagnosticEvent.Version` — event schema version (currently 1)
- `PluginMetadata.Version` — plugin API version
- Old plugins gracefully ignore events with higher versions

## Pluggability

The framework is fully pluggable:

```bash
# Build without Diagnostics
dotnet build src/SharpEmu.CLI/SharpEmu.CLI.csproj  # SUCCESS

# Build with Diagnostics
dotnet build SharpEmu.slnx  # SUCCESS
```
