<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Handoff notes — HLE compatibility work

Context dump for whoever (human or AI) picks this up next, written at the point
where testing moves from "no game available" to "running real games locally."
Delete this file once it's stale/no longer useful.

## Where things stand

- Working branch: `claude/emu-compatibility-fixes-mgd423`, based on `main` @ `9d88542`.
- PR open: https://github.com/Union-Crax/sharpemu/pull/1 (not yet merged, not yet
  reviewed as of this writing).
- 4 commits on the branch, all pushed:
  1. `ffb15a5` — 36 exports: pread/pwrite, readv/writev, fsync/ftruncate/truncate,
     access/rename, mmap/munmap/sceKernelMmap, msync/madvise/mlock, getpagesize,
     sigprocmask/pthread_sigmask, sched_yield, sceKernelIsProspero/GetCpumode/
     GetSystemSwVersion, pthread_detach (posix flavor).
  2. `052be2e` — 31 more: dup/dup2/fcntl, chdir/getcwd/fchdir/realpath, poll,
     getuid family, umask, getrlimit/setrlimit/getrusage, sysconf, sigaction,
     pthread_atfork, clock_getres, flock/fchmod/utimes/futimes, shm_open/shm_unlink.
  3. `0b52190` — pipe/socketpair (in-memory channel table, wired into the shared
     read/write/close paths) + select (real FreeBSD fd_set bitmask semantics).
  4. `8feffb6` — `scripts/report_missing_exports.py` + `docs/hle-export-coverage.md`,
     a generated queue of ~115 more catalog-confirmed symbols worth implementing
     next (kernel AIO, equeue timer events, posix pthread accessors, libc ctype/time,
     dlopen family, etc.) each with its precomputed NID.

All of this was written and verified **without ever running a real game** — every
new export was checked against `scripts/check_sysabi_aerolib.py` (NID correctness)
and covered by unit tests in `tests/SharpEmu.Libs.Tests/Kernel/PosixKernelExportsTests.cs`
(40+ tests using `CpuContext` + a `FakeGuestAddressSpaceMemory`/`FakeCpuMemory` test
double, no real ELF/game loaded). That's the ceiling of what's testable without a game
— logic correctness, register conventions, NID correctness. It is **not** proof any
game actually gets further. That's the next step.

## What "testing with a real game" should actually do

1. **Rebase/merge check first.** If `main` moved since `9d88542`, rebase the branch
   before testing so you're not chasing bugs that were already fixed upstream.
2. Run a game per `README.md` (`.\SharpEmu "eboot.bin" 2>&1 | Tee-Object -FilePath "log.txt"`
   on Windows — this repo's primary target is Windows, this session worked in a Linux
   container which is why nothing end-to-end ran).
3. Grep the log for these patterns — they are the actual compatibility signal:
   - `[LOADER][WARN] Import#... unresolved: nid=...` — a completely missing export.
     Cross-reference the NID against `docs/hle-export-coverage.md`'s curated tier or
     regenerate it (`python3 scripts/report_missing_exports.py`) to find the symbol name,
     or reverse the NID by testing candidate names through `name2nid()` in
     `scripts/generate_aerolib_binary.py`.
   - `[LOADER][WARN] Import#... result: ORBIS_GEN2_ERROR_...` — an export exists but
     returned an error the game didn't expect. This is different from "missing" and
     needs a different fix (usually a wrong struct field, wrong errno mapping, or a
     guest input this session's implementation didn't anticipate).
   - `[LOADER][WARN] Import#... not implemented for generation ...` — export exists
     but not for the right `Generation` (Gen4 vs Gen5) target.
4. **Env vars that turn on verbose tracing** for the new subsystems (see
   `KernelMemoryCompatExports.Posix.cs` / `KernelPipeCompatExports.cs` for the
   exact checks): `SHARPEMU_LOG_PIPES=1` is new from this session. Pre-existing:
   `SHARPEMU_LOG_SEMA`, `SHARPEMU_LOG_EVENT_FLAG`, `SHARPEMU_LOG_PTHREADS`,
   `SHARPEMU_LOG_PTHREAD_CONDS`, `SHARPEMU_LOG_WIDE`, `SHARPEMU_LOG_FIBER`. Check
   `MainWindow.axaml.cs` GUI "Environment" tab (added in #189) for the full toggle list.

## Known gaps / things I deliberately didn't implement, and why

- **`sceKernelGetAppInfo`, `statfs`/`fstatfs`** — skipped because their struct layouts
  are proprietary/unverified. Guessing field offsets would silently write garbage into
  a struct the game reads real data from, which is a worse failure mode than the current
  clean "missing NID" log line. If you have a real game hitting these, the log + a
  disassembly of the caller (what it does with the returned struct) is exactly what's
  needed to get the layout right.
- **AIO (`sceKernelAio*`)** — 13 functions, likely high-impact for Unreal Engine titles
  (pak/asset streaming), not yet implemented. In the coverage report's curated tier.
  This is a real subsystem (async I/O with a completion/polling model), not a one-line
  stub — expect it to need its own state table similar to `KernelPipeCompatExports.cs`.
- **Equeue timer events** (`sceKernelAddTimerEvent`, `AddHRTimerEvent`, etc.) — engines
  use these for frame timing. Also in the curated tier, not implemented.
- **setjmp/longjmp family** — flagged in the coverage report as needing actual CPU
  register-context save/restore support, not a simple HLE stub. Look at how
  `DirectExecutionBackend` handles register state before attempting this.
- **mmap file-backed mapping is copy-on-map, not lazy/shared.** `MmapCore` in
  `KernelMemoryCompatExports.Posix.cs` copies the file's bytes into guest memory once
  at `mmap()` time. This is fine for read-only data-archive access patterns (the common
  case) but is **not** correct for `MAP_SHARED` writes expected to flush back to the file,
  or for mapping files larger than available memory. If a game does either, this will
  misbehave. Grep for `CopyFileIntoMappedRange` if this needs revisiting.
- **pipe reads use a bounded-wait-then-EAGAIN poll loop**, not a real blocking wait
  registered with the guest scheduler (unlike e.g. semaphores/event flags which use
  `GuestThreadExecution.RequestCurrentThreadBlock`). This was a deliberate simplicity
  tradeoff — if a game does heavy pipe IPC and stalls/busy-loops, this is the first
  place to look. The semaphore/event-flag/pthread-cond implementations in
  `Kernel/Kernel*CompatExports.cs` show the "real" blocking pattern to follow if pipes
  need to be upgraded.

## Codebase orientation (for whoever picks this up cold)

- HLE exports are static methods tagged `[SysAbiExport(Nid=..., ExportName=..., Target=...,
  LibraryName=...)]` on `CpuContext ctx`, registered by reflection in
  `SharpEmu.HLE/ModuleManager.cs`. NIDs are `base64(sha1(name + fixed_salt)[:8])` —
  see `name2nid()` in `scripts/generate_aerolib_binary.py`, and always verify new NIDs
  with `python3 scripts/check_sysabi_aerolib.py` before committing (checks against
  `src/SharpEmu.HLE/Aerolib/aerolib.bin`, generated from `scripts/ps5_names.txt`).
- Convention split I used throughout: POSIX-named exports (`read`, `mmap`, `dup2`...)
  set errno via `KernelRuntimeCompatExports.TrySetErrno` and return `-1` in RAX on
  failure; `sceKernel*`-named exports return an `OrbisGen2Result` error code directly
  in RAX, no errno. Both conventions coexist in the same files — check which one an
  export you're touching uses before copying a pattern.
- File descriptors: `KernelMemoryCompatExports._openFiles` (Dictionary<int, FileStream>)
  is the single fd table for real files. `KernelSocketCompatExports` and (new this
  session) `KernelPipeCompatExports` layer their own fd namespaces on top and are
  consulted in that order by the shared `_read`/`_write`/`close` dispatch — see
  `KernelReadUnderscore`/`KernelWriteUnderscore`/`KernelCloseCore` in
  `KernelMemoryCompatExports.cs` for the exact fallthrough order if adding a 4th
  descriptor kind (e.g. AIO).
- Guest thread blocking: `GuestThreadExecution.RequestCurrentThreadBlock(...)` is the
  real mechanism for "park this guest thread until woken," used by semaphores/event
  flags/pthread mutexes/conds. Anything that just does `Thread.Sleep` in a loop
  (like the pipe/poll/select bounded waits added this session) is a simplification,
  not the "correct" pattern — fine for short waits, wrong for anything a game expects
  to block on indefinitely.
- Tests: `tests/SharpEmu.Libs.Tests/` uses xUnit with a `FakeCpuMemory`/custom
  `IGuestAddressSpace` fake (see `PosixKernelExportsTests.FakeGuestAddressSpaceMemory`)
  instead of a real ELF/guest process. No test in this repo currently loads and runs an
  actual game binary — that only happens via the CLI/GUI apps at runtime.
- Build/test commands used this session (.NET 10 SDK, installed manually into
  `/root/.dotnet` since it wasn't preinstalled in the container):
  ```
  dotnet build SharpEmu.slnx
  dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj
  python3 scripts/check_sysabi_aerolib.py
  ```

## Suggested next steps, in priority order

1. Run whatever games you have locally against this branch, collect logs.
2. Grep logs for `unresolved`/`not implemented`/error-result warnings, triage against
   `docs/hle-export-coverage.md`.
3. Report back (or fix directly) with **actual NIDs and context** from the logs —
   that's the one thing this session couldn't produce without a game, and it's the
   highest-leverage input for the next round of work.
4. If a game gets meaningfully further (new stage reached, new error surfaced), that's
   worth a note in `README.md`'s "Games Tested" section per the existing convention.
