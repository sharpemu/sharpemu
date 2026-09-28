<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Import results and diagnostic logging

## Scope

This guide describes import-result warnings and related diagnostic switches.
An import is a function that guest code calls in an emulated library.
A NID identifies the imported function.

Logging filters do not change the result returned to the guest.
They do not fix a failed call or make an unsupported operation succeed.
An absent warning is not proof that an operation succeeded.

## Import-result warning rules

The import dispatcher can write a line with this prefix:

```text
[LOADER][WARN] Import#...
```

The line contains the result, NID, arguments, and return address.
The import number is a dispatch sequence number. It is not the count of that error.

| Result category | Default | With `SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS=1` |
| --- | --- | --- |
| Listed expected negative results | Hidden | Sampled |
| Mutex errors listed below | Sampled | Sampled |
| Other negative results that reach this filter | Shown | Shown |
| Success and positive return values | No import-result warning | No import-result warning |

These rules apply to import-result warnings only. A library, GPU component, or
trace channel can report the same event through a separate message.

## Sampling: first eight, then every 10,000th

The sampler shows occurrences 1 through 8, then 10,000, 20,000, and so on.
It does not show occurrence 10,008 as the next sample.

Each NID and numeric result code has a separate counter. Different error codes
from one NID do not share a counter. Two NIDs for the same function name also
have separate counters. Threads that use the same execution backend share them.
The backend clears the counters when it resets its execution state.

Only calls that reach the sampler increase these counters. Expected results
hidden by the switch do not increase them. The sampler does not retain skipped
messages for later output. It does not provide a complete error count in the log.

## Mutex errors sampled without an extra switch

| Function | NID | Result |
| --- | --- | --- |
| `scePthreadMutexLock` | `9UK1vLZQft4` | `ORBIS_GEN2_ERROR_DEADLOCK` |
| `pthread_mutex_lock` | `7H0iTOciTLo` | `ORBIS_GEN2_ERROR_DEADLOCK` |
| `scePthreadMutexUnlock` | `tn3VlD0hG60` | `ORBIS_GEN2_ERROR_INVALID_ARGUMENT`, `ORBIS_GEN2_ERROR_PERMISSION_DENIED` |
| `pthread_mutex_unlock` | `2Z+PpY6CaJg` | `ORBIS_GEN2_ERROR_INVALID_ARGUMENT`, `ORBIS_GEN2_ERROR_PERMISSION_DENIED` |

These warnings use the first-eight and every-10,000th rule by default.
The expected-result switch neither hides them nor makes them unsampled.
Sampling reduces repeated output. It does not classify these failures as harmless.

These sampled mutex warnings include `occurrence=N`. For example,
`occurrence=10000` identifies the 10,000th result for that NID and result code.
Expected-result warnings for other operations do not include this field.

## Expected results controlled by the switch

The filter uses exact NID and result pairs. It does not hide all errors from a library.

| Operation | NID | Filtered result |
| --- | --- | --- |
| File stat | `eV9wAD2riIA` | `ORBIS_GEN2_ERROR_NOT_FOUND` |
| File open | `1G3lF1Gg1k8` | `ORBIS_GEN2_ERROR_NOT_FOUND` |
| APR path resolution | `gEpBkcwxUjw` | `ORBIS_GEN2_ERROR_NOT_FOUND` |
| Event-queue wait | `fzyMKs9kim0` | `ORBIS_GEN2_ERROR_TIMED_OUT` |
| Mutex trylock | `K-jXhbt2gn4`, `upoVrzMHFeE` | `ORBIS_GEN2_ERROR_BUSY` |
| Semaphore trywait | `H2a+IN9TP0E` | `ORBIS_GEN2_ERROR_TRY_AGAIN` |
| Semaphore poll | `12wOHk8ywb0` | `ORBIS_GEN2_ERROR_BUSY` |
| Network accept | `PIWqhn9oSxc` | `0x80410123` |
| User-service event poll | `yH17Q6NWtVg` | `0x80960007` |
| Privacy-setting query | `D-CzAxQL0XI` | `0x80960009` |
| PlayGo chunk enumeration | `uWIYLFkkwqk` | `0x80B2000C` |

An expected result can still indicate a problem in context. For example, a missing
optional file can be normal, but a missing required file can stop loading.

The filter also contains a condition for `pthread_cond_timedwait`, NID
`27bAgiJmOh0`, with result 60. The earlier positive-result check excludes this value.
Setting the switch does not make that positive timeout produce an import warning.

## Run with expected-result logging

Set the variable before starting a fresh emulator process:

```powershell
$env:SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS = "1"
.\SharpEmu.exe
```

Remove it before the next run to restore the default:

```powershell
Remove-Item Env:SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS -ErrorAction SilentlyContinue
```

Shell variables remain set for later launches from that shell. They do not expire
after one run. Check GUI environment settings if a variable remains active.

## Choose a diagnostic for the question

| Diagnostic | Use |
| --- | --- |
| `SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS=1` | Inspect the listed expected import results. |
| `SHARPEMU_LOG_IO=1` | Inspect file operations and path handling. |
| `SHARPEMU_LOG_DIRECT_MEMORY=1` | Inspect direct-memory operations. |
| `SHARPEMU_LOG_AGC=1` | Inspect graphics command processing. Output can be large. |
| `SHARPEMU_LOG_PTHREAD_MUTEX_FILTER` | Select mutex addresses for targeted tracing. |
| `SHARPEMU_LOG_PTHREAD_FASTPATH=1` | Inspect mutex setup and fast-path details. |
| `SHARPEMU_LOG_VIDEOOUT_FPS=1` | Record periodic frame-rate information. |
| `SHARPEMU_PROFILE_PERFORMANCE=1` | Record performance summaries, including FPS. |
| `SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE=1` | Add frame tracing when the main performance profile is enabled. |

The first-eight and every-10,000th rule is not a global logging rule.
Other channels have their own limits. Pthread trace switches control separate
messages from the kernel compatibility layer.

Enable only the diagnostics needed for the test. Logging and profiling can reduce
FPS and change thread timing. Compare the same scene with the same switches.
Keep capture tools and dump settings the same between comparison runs.

Close normally when possible so buffered reports can be written. Before sharing
a log, inspect it for local paths, user names, and guest data. Do not assume that
shader dumps or crash dumps are suitable for public distribution.

## Source and related guides

- Import filtering: `DirectExecutionBackend.Imports.cs`, methods
  `ShouldLogImportResult` and `ShouldSampleImportResult`.
- Pthread tracing: `KernelPthreadCompatExports.cs`.
- [Environment variable reference](sharpemu-gui-undocumented-env-vars.md).
- [Guest ownership trace guide](guest-ownership-trace.md).
