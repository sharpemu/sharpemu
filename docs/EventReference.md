# Diagnostic Event Reference

All events implement `IDiagnosticEvent` with these base fields:

| Field | Type | Description |
|---|---|---|
| `Timestamp` | long | Monotonic Stopwatch ticks |
| `Version` | int | Event schema version (currently 1) |
| `Category` | string | Event category |
| `Type` | string | Event type within category |

## Events

### BootEvent
| Field | Type | Description |
|---|---|---|
| StageName | string | "Loader", "TLS", "CRT", "Imports", "VideoOut", "GPU" |
| Success | bool | Whether the stage succeeded |
| Detail | string? | Optional detail |

Category: `boot`, Type: `stage`

### ImportEvent
| Field | Type | Description |
|---|---|---|
| Nid | string | NID of the called import |
| ExportName | string? | Resolved export name |
| Library | string? | Library name |
| Result | int | Return value (0 = success) |
| DurationMicros | long | Call duration in microseconds |

Category: `import`, Type: `call`

### CpuEvent
| Field | Type | Description |
|---|---|---|
| Rip | ulong | Instruction pointer |
| Opcode | byte[] | Raw opcode bytes (up to 16) |
| Registers | ulong[]? | Register snapshot (optional) |

Category: `cpu`, Type: `instruction`

### MemoryEvent
| Field | Type | Description |
|---|---|---|
| Operation | string | "Allocate", "Free", "Map", "Unmap", "Protect" |
| Address | ulong | Memory address |
| Size | ulong | Size in bytes |
| Detail | string? | Optional detail |

Category: `memory`, Type: (same as Operation)

### ThreadEvent
| Field | Type | Description |
|---|---|---|
| ThreadId | ulong | Guest thread ID |
| Operation | string | "Create", "Sleep", "Wake", "Exit", "Mutex", "Semaphore" |
| Detail | string? | Optional detail |

Category: `thread`, Type: (same as Operation)

### CrashEvent
| Field | Type | Description |
|---|---|---|
| Rip | ulong | Instruction pointer at crash |
| FaultAddress | ulong | Address that caused the fault |
| Signal | int | Signal number (11=SEGV, 4=ILL, etc.) |
| CrashType | string | "NULL_POINTER", "SIGSEGV", "SIGILL", etc. |
| Registers | Dictionary<string, ulong>? | Register dump |

Category: `crash`, Type: `fault`

### GpuEvent
| Field | Type | Description |
|---|---|---|
| Operation | string | "Submit", "Draw", "Flip", "Present", "Fence" |
| Address | ulong? | GPU address (if applicable) |
| Detail | string? | Optional detail |

Category: `gpu`, Type: (same as Operation)
