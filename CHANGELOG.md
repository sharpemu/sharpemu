# Changelog

## v5 (2026-07-20) — Production Ready

### Architecture
- **EventBus**: thread-safe event dispatcher (ConcurrentBag + Interlocked)
- **PluginRegistry**: Register<T>() with dependency checking
- **DiagnosticManager**: minimal API (Start/Stop/Publish/Flush)
- **DiagnosticClock**: single monotonic time source
- **EventFilter**: category-based filtering (SHARPEMU_DIAG_FILTER)
- **DiagnosticConfig**: env vars + diagnostics.json support
- **RingBuffer<T>**: generic lock-free ring buffer
- **DiagnosticExporter**: JSON, Text, Markdown output

### Contracts (API Frozen)
- IDiagnosticPlugin: Metadata + Initialize/OnEvent/Shutdown
- IDiagnosticEvent: Timestamp + Version + Category + Type
- IDiagnosticContext: GameId + SessionDirectory + Publish
- PluginMetadata: Name/Version/Description/EnvVar/Priority/Budget

### Plugins (9 total)
- BootTimelinePlugin (SHARPEMU_DIAG_BOOT)
- ImportTimelinePlugin (SHARPEMU_DIAG_IMPORTS)
- FirstFailurePlugin (SHARPEMU_DIAG_FAILURE)
- CpuTracePlugin (SHARPEMU_DIAG_CPU)
- CrashPackagePlugin (SHARPEMU_DIAG_CRASH)
- ThreadTimelinePlugin (SHARPEMU_DIAG_THREADS)
- MemoryTimelinePlugin (SHARPEMU_DIAG_MEMORY)
- StatisticsPlugin (SHARPEMU_DIAG_STATS)
- ConsoleSinkPlugin (SHARPEMU_DIAG_CONSOLE)

### Tests
- 9 unit tests (EventBus, RingBuffer, Config) — all pass

### Build
- Build with Diagnostics: SUCCESS (Release + Debug)
- Build without Diagnostics: SUCCESS (Release + Debug)
- Zero coupling: Core/Libs/HLE reference only Contracts

## v4 (2026-07-19)
- Export layer (JSON/Text/Markdown)
- Plugin Metadata (Priority, PerformanceBudget)
- DiagnosticConfig (env vars + diagnostics.json)
- HOW_TO_CREATE_PLUGIN.md

## v3 (2026-07-19)
- EventBus architecture (plugins don't call each other)
- PluginRegistry
- DiagnosticContext

## v2 (2026-07-19)
- Separate plugin files (each independently enable/disable)
- DiagnosticSession manager

## v1 (2026-07-19)
- Monolithic DebugIntelligenceEngine
- Basic contracts
