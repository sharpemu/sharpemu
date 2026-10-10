// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Reflection;
using System.Text;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

internal enum ShaderCacheOutcome
{
    FromStore,
    Compiled,
    Failed,
    Dictionary,
}

internal static unsafe partial class VulkanVideoPresenter
{
    internal const string ShaderCacheWorkerFlag = "--shader-cache-worker";
    private const int ShaderCacheDictionaryExitCode = 3;
    private const int ShaderCacheItemsPerWorker = 96;
    private const int ShaderCacheMinItemsPerWorker = 8;
    private const int ShaderCacheWorkerThreads = 2;
    private const int ShaderCacheMaxPasses = 4;
    private const string ShaderCacheItemPrefix = "[SHADER CACHE][ITEM] ";
    private const string ShaderCacheDriverPrefix = "[SHADER CACHE][DRIVER] ";
    private const string ShaderCacheBeginPrefix = "[SHADER CACHE][BEGIN] ";
    private const string ShaderCacheDerivePrefix = "[SHADER CACHE][DERIVE] ";
    private static readonly TimeSpan ShaderCacheWorkerStallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ShaderCacheProgressInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShaderCacheSessionInterval = TimeSpan.FromSeconds(30);

    internal static int RunShaderCacheWorker(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 7 || !long.TryParse(arguments[1], CultureInfo.InvariantCulture, out var length) ||
            !int.TryParse(arguments[2], CultureInfo.InvariantCulture, out var first) ||
            !int.TryParse(arguments[3], CultureInfo.InvariantCulture, out var count) || arguments[5] is not ("0" or "1"))
        {
            Console.Error.WriteLine("[SHADER CACHE][ERROR] The precompile worker arguments are invalid.");
            return 2;
        }

        SubmissionScheduler.OnFatal = static message => throw new InvalidOperationException(message);
        using var presenter = new Presenter(1, 1, hidden: true);
        presenter.InitializeShaderCompileDevice();
        return presenter.RunShaderCacheWorker(arguments[0], length, first, count, arguments[4], arguments[5] == "1", arguments[6]);
    }

    internal static int RunShaderPrecompile(string app0Root, bool full)
    {
        SubmissionScheduler.OnFatal = static message => throw new InvalidOperationException(message);
        using var presenter = new Presenter(1, 1, hidden: true);
        presenter.InitializeShaderCompileDevice();
        return presenter.RunStandalonePrecompile(app0Root, full);
    }

    private sealed partial class Presenter
    {
        private const int MaxLoggedShaderCacheFailures = 8;
        private const int MaxForwardedWorkerLines = 32;
        private const int MaxDerivationExamples = 12;

        [ThreadStatic]
        private static bool _lastCaptureReachedDictionary;

        [ThreadStatic]
        private static List<UInt128>? _itemContentKeys;

        private bool _shaderCompileOnly;
        private string? _shaderSeedPath;
        private ShaderCacheFile? _shaderCache;
        private ulong _shaderCacheStamp;
        private ulong _shaderCacheDriverStamp;
        private ulong _shaderCacheBinaryKey;
        private ShaderCompileHostFlags _shaderCacheFlags;
        private readonly ConcurrentDictionary<ulong, Lazy<CompiledStageRecord?>> _warmStages = new();
        private readonly CancellationTokenSource _shaderCacheCancel = new();
        private readonly ConcurrentDictionary<int, Process> _shaderCacheWorkers = new();
        private Thread? _shaderCacheThread;
        private volatile bool _shaderCacheDriverMismatch;
        private int _shaderCacheFailed;
        private int _shaderCacheFromBinaries;
        private int _shaderCacheCompiled;
        private int _shaderCacheBinaryRejected;
        private int _shaderCacheUnoptimized;
        private Timer? _shaderCacheSessionTimer;
        private (int FromStore, int Compiled, int Rejected) _shaderCacheSessionReported;
        private int _shaderSeedRecordsSaved = -1;
        private int _precompileComputeTotal;
        private int _precompileGraphicsTotal;
        private int _precompileComputeDone;
        private int _precompileGraphicsDone;
        private int _precompileFromStore;
        private int _precompileCompiled;
        private int _precompileFailed;
        private int _precompileDictionary;
        private int _precompileForwardedLines;
        private long _precompileStarted;
        private readonly ConcurrentDictionary<string, int> _derivationResults = new();
        private readonly ConcurrentDictionary<string, int> _derivationDifferences = new();
        private readonly ConcurrentQueue<string> _derivationExamples = new();

        ShaderCacheFile? IShaderPipelineHost.ShaderCache => Volatile.Read(ref _shaderCache);

        internal void InitializeShaderCompileDevice()
        {
            _shaderCompileOnly = true;
            _relay.BindCurrentThread();
            _vk = Vk.GetApi();
            uint loaderVersion = Vk.Version10;
            Check(_vk.EnumerateInstanceVersion(ref loaderVersion), "vkEnumerateInstanceVersion");
            if (loaderVersion < Vk.Version13)
            {
                throw new InvalidOperationException("SharpEmu requires Vulkan 1.3.");
            }

            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreatePipelineCache();
            InitializeShaderCacheStamps();
        }

        private void InitializeShaderCacheStamps()
        {
            _shaderCacheFlags = ShaderCompileHostFlags.From(this);
            _shaderCacheStamp = ShaderCacheStamp(GuestGpu.Current, _shaderCacheFlags);
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            var device = new byte[16 + 3 * sizeof(uint) + sizeof(ulong) + 1];
            new ReadOnlySpan<byte>(properties.PipelineCacheUuid, 16).CopyTo(device);
            BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(16), properties.VendorID);
            BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(20), properties.DeviceID);
            BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(24), properties.DriverVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(device.AsSpan(28), _shaderCacheStamp);
            device[36] = _pipelineBinaries is null ? (byte)0 : (byte)1;
            var pipelineState = string.Join('|',
                _colorWriteEnableApi is not null, _supportsDepthClipControl, _supportsDepthClipEnable, _supportsDepthBounds,
                _supportsFillRectangle, _supportsShaderClipDistance, _maxPushDescriptors, _maxColorAttachments);
            _shaderCacheDriverStamp = XxHash3.HashToUInt64([.. device, .. Encoding.UTF8.GetBytes(pipelineState)]);
            _shaderCacheBinaryKey = _pipelineBinaries is { } api ? ShaderCacheFile.DriverKeyHash(api.DriverKey) : 0;
        }

        private void StartShaderCache()
        {
            if (!StartShaderCacheCore())
            {
                SetShaderCacheState(false);
            }
        }

        private bool StartShaderCacheCore()
        {
            var title = VideoOutExports.GetApplicationDisplayName();
            if (!ShaderCacheSettings.Enabled)
            {
                Console.Error.WriteLine($"[SHADER CACHE] {title}: off.");
                return false;
            }

            if (_pipelineCacheShardDirectory is not null)
            {
                Console.Error.WriteLine($"[SHADER CACHE] {title}: off, the driver cache shards own pipeline caching on this platform.");
                return false;
            }

            if (ShaderCacheDirectory(VideoOutExports.GetApplicationTitleId()) is not { } directory)
            {
                return false;
            }

            InitializeShaderCacheStamps();
            _shaderSeedPath = ShaderCacheSettings.SeedPath(VideoOutExports.GetApplicationTitleId());
            var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
            var seedExists = File.Exists(_shaderSeedPath);
            var full = ShaderCacheSettings.FullPrecompile || seedExists;
            SetShaderCacheState(true);
            _precompileStarted = Stopwatch.GetTimestamp();
            UpdateShaderCacheProgress(ShaderCachePhase.Loading);
            _shaderCacheThread = new Thread(() =>
            {
                try
                {
                    // Disk indexing must not stop the splash's render/event loop.
                    var file = ShaderCacheFile.Open(directory, (done, total) =>
                    {
                        _shaderCacheCancel.Token.ThrowIfCancellationRequested();
                        UpdateShaderCacheProgress(ShaderCachePhase.Loading, done, total);
                    });
                    if (file is null)
                    {
                        return;
                    }

                    UseShaderCacheStores(file, file);
                    ShaderInventory.Attach(file, null);
                    Volatile.Write(ref _shaderCache, file);
                    if (file.ImportedLegacyComputes > 0)
                    {
                        Console.Error.WriteLine($"[SHADER CACHE] {title}: imported {file.ImportedLegacyComputes} compute pipelines from the old prewarm list.");
                    }
                    LogShaderCacheSummary(title, file);
                    if (ShaderCacheSettings.Learn && !seedExists)
                    {
                        Console.Error.WriteLine(
                            $"[SHADER CACHE] {title}: learning run, nothing is precompiled now. The shader seed is written to {_shaderSeedPath} " +
                            "while you play, and the next launch prepares every shader and pipeline from it.");
                        return;
                    }

                    Precompile(file, title, app0Root, full, _shaderCacheCancel.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[SHADER CACHE][WARN] {title}: the shader precompile stopped: {exception.Message}");
                }
                finally
                {
                    Volatile.Write(ref _shaderCacheProgress, null);
                    WakeRenderThread();
                    SetShaderCacheState(false);
                    if (!_shaderCacheCancel.IsCancellationRequested)
                    {
                        StartShaderCacheSession(title);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "SharpEmu shader precompile",
            };
            _shaderCacheThread.Start();
            return true;
        }

        internal int RunStandalonePrecompile(string app0Root, bool full)
        {
            var title = VideoOutExports.GetApplicationDisplayName();
            if (ShaderCacheDirectory(VideoOutExports.GetApplicationTitleId()) is not { } directory ||
                ShaderCacheFile.Open(directory) is not { } file)
            {
                return 1;
            }

            using (file)
            {
                _shaderSeedPath = ShaderCacheSettings.SeedPath(VideoOutExports.GetApplicationTitleId());
                UseShaderCacheStores(file, file);
                LogShaderCacheSummary(title, file);
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler stop = (_, arguments) =>
                {
                    arguments.Cancel = true;
                    cancel.Cancel();
                    KillShaderCacheWorkers();
                };
                Console.CancelKeyPress += stop;
                try
                {
                    Precompile(file, title, app0Root, full, cancel.Token);
                    CompactShaderCache(file);
                    return _shaderCacheDriverMismatch ? 1 : 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine($"[SHADER CACHE] {title}: precompile stopped; finished work is kept and the next run continues.");
                    return 1;
                }
                finally
                {
                    Console.CancelKeyPress -= stop;
                }
            }
        }

        private ShaderSeed LoadShaderSeed() =>
            _shaderSeedPath is { Length: > 0 } path ? ShaderSeed.Load(path) : new ShaderSeed();

        private void SaveShaderSeed(ShaderCacheFile file, string title)
        {
            if (!ShaderCacheSettings.Learn || _shaderSeedPath is not { Length: > 0 } path)
            {
                return;
            }

            var existing = ShaderSeed.Load(path);
            var merged = existing.MergedWith(ShaderSeed.Learn(file))
                .Stamped(VideoOutExports.GetApplicationTitleId(), VideoOutExports.GetApplicationVersion());
            if (merged.IsEmpty || merged.Serialize().AsSpan().SequenceEqual(existing.Serialize()))
            {
                return;
            }

            if (merged.TrySave(path))
            {
                Console.Error.WriteLine(
                    $"[SHADER CACHE] {title}: shader seed {merged.StateCount} graphics states in {merged.GroupCount} passes, " +
                    $"{merged.Pairs.Count} shader pairs, played pipelines compute {merged.Computes.Count}, graphics {merged.Graphics.Count} " +
                    $"(was {existing.StateCount} states, {existing.Pairs.Count} pairs, {existing.Computes.Count + existing.Graphics.Count} pipelines) at {path}");
            }
        }

        private static string? ShaderCacheDirectory(string titleId) =>
            Path.GetDirectoryName(VulkanPipelineCacheStorage.ResolvePath(
                titleId,
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")));

        private void UseShaderCacheStores(ShaderCacheFile? target, params ShaderCacheFile[] sources) =>
            SetPipelineStore(target, sources);

        private void LogShaderCacheSummary(string title, ShaderCacheFile file)
        {
            int compute = 0, vertex = 0, pixel = 0, scanned = 0;
            var programs = file.SnapshotPrograms();
            foreach (var program in programs)
            {
                if (!StaticStageInputs.TryParse(program, out var header))
                {
                    continue;
                }

                scanned += program.Program.Source == InventorySource.Scanned ? 1 : 0;
                switch (header.Type)
                {
                    case AgcShaderType.Compute:
                        compute++;
                        break;
                    case AgcShaderType.Geometry:
                        vertex++;
                        break;
                    case AgcShaderType.Pixel:
                        pixel++;
                        break;
                }
            }

            var binaries = _pipelineBinaries is null ? "unsupported by the driver" : file.BinaryCount.ToString(CultureInfo.InvariantCulture);
            var seed = LoadShaderSeed();
            var learned = ShaderSeed.Learn(file).MergedWith(seed);
            var version = VideoOutExports.GetApplicationVersion();
            if (!seed.IsEmpty && seed.GameVersion.Length != 0 && version.Length != 0 &&
                !string.Equals(seed.GameVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[SHADER CACHE][WARN] {title}: the shader seed was made with game version {seed.GameVersion}, this is {version}; " +
                    "shaders that changed between the versions are not paired.");
            }

            Console.Error.WriteLine(
                $"[SHADER CACHE] {title}: shaders compute {compute}, vertex {vertex}, pixel {pixel} " +
                $"({scanned} from game files, {programs.Length - scanned} from the running game); " +
                $"recorded pipelines compute {file.Computes.Count}, graphics {file.Graphics.Count}; " +
                $"graphics states {learned.StateCount} in {learned.GroupCount} passes " +
                $"({(seed.IsEmpty ? "no seed" : $"seed {seed.StateCount} states, {seed.Pairs.Count} pairs, {seed.Computes.Count + seed.Graphics.Count} played pipelines")}); " +
                $"pipeline binaries {binaries}; {FormatCacheBytes(file.Length)} at {file.Path}");
        }

        private void Precompile(ShaderCacheFile file, string title, string? app0Root, bool full, CancellationToken cancellation)
        {
            if (_precompileStarted == 0) _precompileStarted = Stopwatch.GetTimestamp();
            MergeLeftoverParts(file);
            if (!string.IsNullOrEmpty(app0Root) && Directory.Exists(app0Root))
            {
                ScanGameFiles(file, title, app0Root, cancellation);
            }

            var prepared = false;
            var complete = false;
            for (var pass = 0; !_shaderCacheDriverMismatch; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                UpdateShaderCacheProgress(ShaderCachePhase.Preparing);
                var length = file.Length;
                List<ShaderWorkItem> pending;
                using (var snapshot = ShaderCacheFile.OpenSnapshot(file.Path, length))
                {
                    if (snapshot is null)
                    {
                        return;
                    }

                    var list = ShaderWorkList.Build(snapshot, full, LoadShaderSeed());
                    pending = list.Items.Where(item => !snapshot.IsDone(item.Identity, _shaderCacheDriverStamp)).ToList();
                    if (pass == 0)
                    {
                        LogWorkList(title, list, pending, full);
                    }
                }

                if (pending.Count == 0)
                {
                    complete = true;
                    break;
                }

                if (pass == ShaderCacheMaxPasses)
                {
                    break;
                }

                var doneBefore = Volatile.Read(ref _precompileComputeDone) + Volatile.Read(ref _precompileGraphicsDone);
                _precompileComputeTotal = pending.Count(static item => item.IsCompute);
                _precompileGraphicsTotal = pending.Count - _precompileComputeTotal;
                _precompileComputeDone = _precompileGraphicsDone = 0;
                Volatile.Write(ref _precompilePass, pass + 1);
                RunPrecompileWorkers(file, title, length, pending.Count, full, cancellation);
                prepared = true;
                if (Volatile.Read(ref _precompileComputeDone) + Volatile.Read(ref _precompileGraphicsDone) == 0 && doneBefore == 0)
                {
                    break;
                }
            }

            if (prepared)
            {
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[SHADER CACHE] {title}: precompile finished in {Stopwatch.GetElapsedTime(_precompileStarted).TotalSeconds:F1} s; " +
                    $"pipelines compiled {Volatile.Read(ref _precompileCompiled)}, already stored {Volatile.Read(ref _precompileFromStore)}, " +
                    $"skipped {Volatile.Read(ref _precompileFailed)} the translator cannot compile; pipeline binaries {file.BinaryCount}; {FormatCacheBytes(file.Length)}"));
                LogDerivationCheck(title);
            }
            else
            {
                Console.Error.WriteLine($"[SHADER CACHE] {title}: every known shader is already prepared.");
            }

            if (complete && prepared)
            {
                CompactShaderCache(file, dropUnreferencedBinaries: true);
            }
        }

        private void LogDerivationCheck(string title)
        {
            if (_derivationResults.IsEmpty)
            {
                return;
            }

            int Count(string key) => _derivationResults.GetValueOrDefault(key);
            var differences = string.Join(", ", _derivationDifferences
                .OrderByDescending(static entry => entry.Value)
                .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => $"{entry.Key} {entry.Value}"));
            Console.Error.WriteLine(
                $"[SHADER CACHE] {title}: shader-file derivation of played pipelines: " +
                $"compute same {Count("compute same")}, differs {Count("compute differs")}, not derivable {Count("compute underivable")}; " +
                $"graphics same {Count("graphics same")} (outside the learned states {Count("graphics uncovered")}), " +
                $"differs {Count("graphics differs")}, not derivable {Count("graphics underivable")}" +
                (differences.Length == 0 ? "." : $"; differing fields: {differences}."));
            foreach (var example in _derivationExamples)
            {
                Console.Error.WriteLine($"{ShaderCacheDerivePrefix}{example}");
            }
        }

        private void MergeLeftoverParts(ShaderCacheFile file)
        {
            var directory = Path.GetDirectoryName(file.Path)!;
            var parts = Directory.GetFiles(directory, Path.GetFileName(file.Path) + ".part*");
            UpdateShaderCacheProgress(ShaderCachePhase.Recovering, 0, parts.Length);
            var recovered = 0;
            foreach (var part in parts)
            {
                file.MergePart(part);
                UpdateShaderCacheProgress(ShaderCachePhase.Recovering, ++recovered, parts.Length);
                try
                {
                    File.Delete(part);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        private void ScanGameFiles(ShaderCacheFile file, string title, string app0Root, CancellationToken cancellation)
        {
            var started = Stopwatch.GetTimestamp();
            var lastReport = started;
            UpdateShaderCacheProgress(ShaderCachePhase.Scanning);
            var result = GameShaderScanner.Scan(app0Root, file, (done, total) =>
            {
                UpdateShaderCacheProgress(ShaderCachePhase.Scanning, done, total);
                var now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReport), now) < ShaderCacheProgressInterval)
                {
                    return;
                }

                Interlocked.Exchange(ref lastReport, now);
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[SHADER CACHE] {title}: scanning game files {done / (1024.0 * 1024.0 * 1024.0):F1}/{total / (1024.0 * 1024.0 * 1024.0):F1} GB"));
            }, cancellation);
            if (result.FilesScanned != 0)
            {
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[SHADER CACHE] {title}: scanned {result.FilesScanned} game files ({result.FilesSkipped} unchanged) " +
                    $"in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s; {result.Programs} new shaders found."));
            }
        }

        private static void LogWorkList(string title, ShaderWorkList list, List<ShaderWorkItem> pending, bool full)
        {
            int Count(IEnumerable<ShaderWorkItem> items, ShaderWorkKind kind) => items.Count(item => item.Kind == kind);
            Console.Error.WriteLine(
                $"[SHADER CACHE] {title}: to prepare compute {pending.Count(static item => item.IsCompute)} " +
                $"(recorded {Count(pending, ShaderWorkKind.RecordedCompute)}, seed {Count(pending, ShaderWorkKind.SeedCompute)}, " +
                $"from shaders {Count(pending, ShaderWorkKind.StaticCompute)}), " +
                $"graphics {pending.Count(static item => !item.IsCompute)} " +
                $"(recorded {Count(pending, ShaderWorkKind.RecordedGraphics)}, seed {Count(pending, ShaderWorkKind.SeedGraphics)}, " +
                $"learned {Count(pending, ShaderWorkKind.StaticGraphics)}); " +
                $"already prepared {list.Items.Count - pending.Count}." +
                (full
                    ? $" Graphics states {list.States.Count}."
                    : " Learned graphics pipelines are prepared when a shader seed is present, with learning on, or by a full precompile."));
        }

        private void RunPrecompileWorkers(ShaderCacheFile file, string title, long length, int pendingCount, bool full, CancellationToken cancellation)
        {
            var batches = new ConcurrentQueue<(int First, int Count)>();
            var batchSize = Math.Clamp(
                (pendingCount + ShaderCacheSettings.PrecompileJobs - 1) / ShaderCacheSettings.PrecompileJobs,
                ShaderCacheMinItemsPerWorker, ShaderCacheItemsPerWorker);
            for (var first = 0; first < pendingCount; first += batchSize)
            {
                batches.Enqueue((first, Math.Min(batchSize, pendingCount - first)));
            }

            var jobs = Math.Min(ShaderCacheSettings.PrecompileJobs, batches.Count);
            Volatile.Write(ref _precompileBatches, batches.Count);
            Volatile.Write(ref _precompileMerged, 0);
            Volatile.Write(ref _precompileWorkersActive, jobs);
            UpdateShaderCacheProgress(ShaderCachePhase.Compiling);
            using var progress = new Timer(_ => ReportPrecompileProgress(title, jobs), null, ShaderCacheProgressInterval, ShaderCacheProgressInterval);
            var threads = new Thread[jobs];
            for (var slot = 0; slot < jobs; slot++)
            {
                var part = file.Path + ".part" + slot.ToString(CultureInfo.InvariantCulture);
                threads[slot] = new Thread(() =>
                {
                    try
                    {
                        while (!cancellation.IsCancellationRequested && !_shaderCacheDriverMismatch && batches.TryDequeue(out var batch))
                        {
                            RunPrecompileWorker(file, length, batch.First, batch.Count, part, full, cancellation);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _precompileWorkersActive);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"SharpEmu shader precompile {slot}",
                };
                threads[slot].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            cancellation.ThrowIfCancellationRequested();
        }

        private void RunPrecompileWorker(ShaderCacheFile file, long length, int first, int count, string part, bool full, CancellationToken cancellation)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The emulator path is unknown."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase) &&
                Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            {
                start.ArgumentList.Add(entry);
            }

            foreach (var argument in (string[])[
                         ShaderCacheWorkerFlag, file.Path, length.ToString(CultureInfo.InvariantCulture),
                         first.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture), part, full ? "1" : "0",
                         _shaderSeedPath ?? string.Empty])
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment.Remove("SHARPEMU_RENDERDOC_WAIT");
            start.Environment.Remove("SHARPEMU_RENDERDOC_CAPTURE");
            var driverCache = Path.Combine(Path.GetTempPath(), "SharpEmu", "precompile", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(driverCache);
            start.Environment["__GL_SHADER_DISK_CACHE"] = "1";
            start.Environment["__GL_SHADER_DISK_CACHE_PATH"] = driverCache;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The shader precompile worker could not start.");
            _shaderCacheWorkers[process.Id] = process;
            try
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                var inFlight = new ConcurrentDictionary<ulong, byte>();
                var lastActivity = Stopwatch.GetTimestamp();
                process.OutputDataReceived += (_, line) =>
                {
                    Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                    HandleWorkerLine(line.Data, inFlight);
                };
                process.ErrorDataReceived += (_, line) => ForwardWorkerError(line.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var stalled = false;
                while (!process.WaitForExit(250))
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        KillShaderCacheWorker(process);
                        break;
                    }

                    if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) > ShaderCacheWorkerStallTimeout)
                    {
                        stalled = true;
                        KillShaderCacheWorker(process);
                        break;
                    }
                }

                process.WaitForExit();
                if (stalled)
                {
                    foreach (var identity in inFlight.Keys)
                    {
                        file.AddDone(identity, _shaderCacheDriverStamp);
                        Interlocked.Increment(ref _precompileFailed);
                        Console.Error.WriteLine(
                            $"[SHADER CACHE][WARN] Pipeline 0x{identity:X16} did not finish in {ShaderCacheWorkerStallTimeout.TotalMinutes:F0} minutes and is skipped.");
                    }

                    return;
                }

                if (process.ExitCode is not (0 or ShaderCacheDictionaryExitCode) && !cancellation.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[SHADER CACHE][WARN] A precompile worker exited with {process.ExitCode}.");
                }
            }
            finally
            {
                _shaderCacheWorkers.TryRemove(process.Id, out _);
                try
                {
                    Directory.Delete(driverCache, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                if (File.Exists(part))
                {
                    file.MergePart(part);
                    Interlocked.Increment(ref _precompileMerged);
                    try
                    {
                        File.Delete(part);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private void HandleWorkerLine(string? line, ConcurrentDictionary<ulong, byte> inFlight)
        {
            if (line is null)
            {
                return;
            }

            if (line.StartsWith(ShaderCacheBeginPrefix, StringComparison.Ordinal))
            {
                if (ulong.TryParse(line[ShaderCacheBeginPrefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var started))
                {
                    inFlight.TryAdd(started, 0);
                }

                return;
            }

            if (line.StartsWith(ShaderCacheDerivePrefix, StringComparison.Ordinal))
            {
                NoteDerivation(line[ShaderCacheDerivePrefix.Length..]);
                return;
            }

            if (line.StartsWith(ShaderCacheDriverPrefix, StringComparison.Ordinal))
            {
                var fields = line[ShaderCacheDriverPrefix.Length..].Split(' ');
                if (fields.Length == 2 &&
                    ulong.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var stamp) &&
                    ulong.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var binaryKey) &&
                    (stamp != _shaderCacheDriverStamp || binaryKey != _shaderCacheBinaryKey) && !_shaderCacheDriverMismatch)
                {
                    _shaderCacheDriverMismatch = true;
                    Console.Error.WriteLine(
                        $"[SHADER CACHE][WARN] A precompile worker sees another driver identity " +
                        $"(stamp {stamp:X16}/{_shaderCacheDriverStamp:X16}, pipeline key {binaryKey:X16}/{_shaderCacheBinaryKey:X16}); " +
                        "the precompile stops so it does not rebuild the same pipelines on every launch.");
                }

                return;
            }

            if (!line.StartsWith(ShaderCacheItemPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var parts = line[ShaderCacheItemPrefix.Length..].Split(' ');
            if (parts.Length != 3 || !Enum.TryParse<ShaderCacheOutcome>(parts[1], out var outcome) ||
                !ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var finished))
            {
                return;
            }

            inFlight.TryRemove(finished, out _);

            if (outcome != ShaderCacheOutcome.Dictionary)
            {
                Interlocked.Increment(ref parts[0] == "compute" ? ref _precompileComputeDone : ref _precompileGraphicsDone);
            }

            switch (outcome)
            {
                case ShaderCacheOutcome.FromStore:
                    Interlocked.Increment(ref _precompileFromStore);
                    break;
                case ShaderCacheOutcome.Compiled:
                    Interlocked.Increment(ref _precompileCompiled);
                    break;
                case ShaderCacheOutcome.Failed:
                    Interlocked.Increment(ref _precompileFailed);
                    break;
                default:
                    Interlocked.Increment(ref _precompileDictionary);
                    break;
            }
        }

        private void NoteDerivation(string result)
        {
            var fields = result.Split(' ', 4);
            if (fields.Length < 3)
            {
                return;
            }

            _derivationResults.AddOrUpdate($"{fields[0]} {fields[1]}", 1, static (_, count) => count + 1);
            if (fields[1] == "differs" && fields.Length == 4)
            {
                _derivationDifferences.AddOrUpdate($"{fields[0]} {RecordDifference.Category(fields[3])}", 1, static (_, count) => count + 1);
            }

            if (fields[1] is "differs" or "underivable" && _derivationExamples.Count < MaxDerivationExamples)
            {
                _derivationExamples.Enqueue(result);
            }
        }

        private void ForwardWorkerError(string? line)
        {
            if (line is not null &&
                (line.Contains("[SHADER CACHE]", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal) ||
                 line.Contains("[FATAL]", StringComparison.Ordinal)) &&
                Interlocked.Increment(ref _precompileForwardedLines) <= MaxForwardedWorkerLines)
            {
                Console.Error.WriteLine(line);
            }
        }

        private void ReportPrecompileProgress(string title, int jobs)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[SHADER CACHE] {title}: precompiling compute {Volatile.Read(ref _precompileComputeDone)}/{_precompileComputeTotal} | " +
                $"graphics {Volatile.Read(ref _precompileGraphicsDone)}/{_precompileGraphicsTotal} | " +
                $"compiled {Volatile.Read(ref _precompileCompiled)} | stored {Volatile.Read(ref _precompileFromStore)} | " +
                $"skipped {Volatile.Read(ref _precompileFailed)} | {jobs} workers | {Stopwatch.GetElapsedTime(_precompileStarted).TotalSeconds:F0} s"));
        }

        private void StartShaderCacheSession(string title)
        {
            Interlocked.Exchange(ref _shaderCacheFromBinaries, 0);
            Interlocked.Exchange(ref _shaderCacheCompiled, 0);
            Interlocked.Exchange(ref _shaderCacheBinaryRejected, 0);
            Interlocked.Exchange(ref _shaderCacheUnoptimized, 0);
            _shaderCacheSessionTimer = new Timer(_ => ReportShaderCacheSession(title), null, ShaderCacheSessionInterval, ShaderCacheSessionInterval);
        }

        private void ReportShaderCacheSession(string title)
        {
            if (Volatile.Read(ref _shaderCache) is { } file &&
                file.SnapshotGraphics().Length + file.SnapshotComputes().Length is var recorded && recorded != _shaderSeedRecordsSaved)
            {
                _shaderSeedRecordsSaved = recorded;
                SaveShaderSeed(file, title);
            }

            var current = (Volatile.Read(ref _shaderCacheFromBinaries), Volatile.Read(ref _shaderCacheCompiled), Volatile.Read(ref _shaderCacheBinaryRejected));
            if (current == _shaderCacheSessionReported)
            {
                return;
            }

            _shaderCacheSessionReported = current;
            Console.Error.WriteLine(
                $"[SHADER CACHE] {title}: this session pipelines from the store {current.Item1}, compiled {current.Item2} " +
                $"({Volatile.Read(ref _shaderCacheUnoptimized)} of them unoptimized first), rejected binaries {current.Item3}.");
        }

        private void KillShaderCacheWorkers()
        {
            foreach (var process in _shaderCacheWorkers.Values)
            {
                KillShaderCacheWorker(process);
            }
        }

        private static void KillShaderCacheWorker(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        internal int RunShaderCacheWorker(string cachePath, long length, int first, int count, string partPath, bool full, string seedPath)
        {
            _shaderSeedPath = seedPath;
            Console.Out.WriteLine($"{ShaderCacheDriverPrefix}{_shaderCacheDriverStamp:X16} {_shaderCacheBinaryKey:X16}");
            using var snapshot = ShaderCacheFile.OpenSnapshot(cachePath, length);
            using var part = ShaderCacheFile.OpenPart(partPath);
            if (snapshot is null || part is null)
            {
                return 1;
            }

            UseShaderCacheStores(part, snapshot, part);
            var list = ShaderWorkList.Build(snapshot, full, LoadShaderSeed());
            var slice = list.Items
                .Where(item => !snapshot.IsDone(item.Identity, _shaderCacheDriverStamp))
                .Skip(first)
                .Take(count)
                .ToArray();
            var next = -1;
            var output = new object();
            var threads = new Thread[Math.Min(ShaderCacheWorkerThreads, Math.Max(slice.Length, 1))];
            for (var index = 0; index < threads.Length; index++)
            {
                threads[index] = new Thread(() =>
                {
                    while (!_pipelineBinaryDictionaryReached)
                    {
                        var position = Interlocked.Increment(ref next);
                        if (position >= slice.Length)
                        {
                            break;
                        }

                        var item = slice[position];
                        lock (output)
                        {
                            Console.Out.WriteLine($"{ShaderCacheBeginPrefix}{item.Identity:X16}");
                        }

                        _itemContentKeys = [];
                        var outcome = ProcessWorkItem(list, item);
                        var derivation = outcome == ShaderCacheOutcome.Dictionary ? null : CheckDerivation(list, item);
                        if (outcome != ShaderCacheOutcome.Dictionary)
                        {
                            part.AddDone(item.Identity, _shaderCacheDriverStamp, _itemContentKeys);
                        }

                        _itemContentKeys = null;
                        lock (output)
                        {
                            if (derivation is not null)
                            {
                                Console.Out.WriteLine($"{ShaderCacheDerivePrefix}{derivation}");
                            }

                            Console.Out.WriteLine($"{ShaderCacheItemPrefix}{(item.IsCompute ? "compute" : "graphics")} {outcome} {item.Identity:X16}");
                        }
                    }
                })
                {
                    Name = $"SharpEmu shader precompile worker {index}",
                };
                threads[index].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Console.Out.Flush();
            return _pipelineBinaryDictionaryReached ? ShaderCacheDictionaryExitCode : 0;
        }

        private ShaderCacheOutcome ProcessWorkItem(ShaderWorkList list, ShaderWorkItem item)
        {
            try
            {
                var file = list.File;
                switch (item.Kind)
                {
                    case ShaderWorkKind.RecordedCompute:
                    {
                        var compute = file.Computes[item.First];
                        return CompileComputeItem(compute.Record, compute.Code);
                    }

                    case ShaderWorkKind.StaticCompute:
                    {
                        var program = file.Programs[item.First];
                        return StaticStageInputs.TryCompute(program, ((IShaderPipelineHost)this).ComputeWave64Supported, out var record)
                            ? CompileComputeItem(record, program.Code)
                            : ShaderCacheOutcome.Failed;
                    }

                    case ShaderWorkKind.SeedCompute:
                    {
                        var compute = list.SeedComputes[item.First];
                        return CompileComputeItem(compute.Record, compute.Code);
                    }

                    case ShaderWorkKind.RecordedGraphics:
                    {
                        var graphics = file.Graphics[item.First];
                        return CompileGraphicsItem(graphics.Record, graphics.VertexCode, graphics.PixelCode, deriveVertexCursor: false);
                    }

                    case ShaderWorkKind.SeedGraphics:
                    {
                        var graphics = list.SeedGraphics[item.First];
                        return CompileGraphicsItem(graphics.Record, graphics.VertexCode, graphics.PixelCode, deriveVertexCursor: false);
                    }

                    default:
                    {
                        var vertex = file.Programs[item.First];
                        var pixel = file.Programs[item.Second];
                        var state = list.States[item.State];
                        if (!StaticStageInputs.TryGraphics(vertex, pixel, state, list.SecondInterpolantVariant, out var vertexRecord, out var pixelRecord))
                        {
                            return ShaderCacheOutcome.Failed;
                        }

                        var record = new GraphicsPipelineRecord
                        {
                            Vertex = vertexRecord,
                            Pixel = pixelRecord,
                            Rendering = state.Rendering,
                            VertexInput = new PipelineVertexInputState(),
                            StaticParameters = state.StaticParameters,
                            Attributes = [],
                        };
                        return CompileGraphicsItem(record, vertex.Code, pixel.Code, deriveVertexCursor: true);
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException or
                                                  ResourcePlanException or IndexOutOfRangeException)
            {
                NoteShaderCacheFailure(item.Kind.ToString(), item.Identity, exception.Message);
                return ShaderCacheOutcome.Failed;
            }
        }

        private string? CheckDerivation(ShaderWorkList list, ShaderWorkItem item)
        {
            try
            {
                switch (item.Kind)
                {
                    case ShaderWorkKind.RecordedCompute:
                    {
                        var compute = list.File.Computes[item.First];
                        return CheckComputeDerivation(list, compute.Record, compute.Code);
                    }

                    case ShaderWorkKind.SeedCompute:
                    {
                        var compute = list.SeedComputes[item.First];
                        return CheckComputeDerivation(list, compute.Record, compute.Code);
                    }

                    case ShaderWorkKind.RecordedGraphics:
                    {
                        var graphics = list.File.Graphics[item.First];
                        return CheckGraphicsDerivation(list, graphics.Record, graphics.VertexCode, graphics.PixelCode);
                    }

                    case ShaderWorkKind.SeedGraphics:
                    {
                        var graphics = list.SeedGraphics[item.First];
                        return CheckGraphicsDerivation(list, graphics.Record, graphics.VertexCode, graphics.PixelCode);
                    }

                    default:
                        return null;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException or
                                                  ResourcePlanException or IndexOutOfRangeException)
            {
                return $"{(item.IsCompute ? "compute" : "graphics")} underivable 0x{item.Identity:X16} {exception.Message}";
            }
        }

        private string CheckComputeDerivation(ShaderWorkList list, StageRecord record, ShaderCodeCapture code)
        {
            var name = $"0x{record.Hash:X16}";
            if (!list.TryGetProgram(record.Hash, record.CodeSize, out var program))
            {
                return $"compute underivable {name} no shader header";
            }

            if (!StaticStageInputs.TryCompute(program, ((IShaderPipelineHost)this).ComputeWave64Supported, out var derived))
            {
                return $"compute underivable {name} header registers";
            }

            return CompareDerivedStage(string.Empty, record, code, derived, program.Code) is { } difference
                ? $"compute differs {name} {difference}"
                : $"compute same {name}";
        }

        private string CheckGraphicsDerivation(
            ShaderWorkList list,
            GraphicsPipelineRecord record,
            ShaderCodeCapture vertexCode,
            ShaderCodeCapture? pixelCode)
        {
            var name = $"vs=0x{record.Vertex.Hash:X16}/ps=0x{record.Pixel?.Hash ?? 0:X16}";
            if (record.Pixel is not { Pixel: { } pixelInputs } pixelRecord || pixelCode is null ||
                record.Vertex.Vertex is not { FetchEmbedded: false } vertexInputs ||
                record.VertexInput.BindingCount != 0 || record.VertexInput.AttributeCount != 0)
            {
                return $"graphics underivable {name} vertex fetch or no pixel shader";
            }

            if (!list.TryGetProgram(record.Vertex.Hash, record.Vertex.CodeSize, out var vertex) ||
                !list.TryGetProgram(pixelRecord.Hash, pixelRecord.CodeSize, out var pixel))
            {
                return $"graphics underivable {name} no shader header";
            }

            var state = ShaderSeed.CreateState(record.Rendering, record.StaticParameters, pixelInputs.Outputs, vertexInputs.ClipSpace);
            if (!StaticStageInputs.TryGraphics(vertex, pixel, state, list.SecondInterpolantVariant, out var derivedVertex, out var derivedPixel))
            {
                return $"graphics underivable {name} header registers";
            }

            if (CompareDerivedStage("pixel", pixelRecord, pixelCode, derivedPixel, pixel.Code) is { } pixelDifference)
            {
                return $"graphics differs {name} {pixelDifference}";
            }

            var derivedPixelStage = GetCompiledStage(derivedPixel, pixel.Code)!;
            if (CompareDerivedStage("vertex", record.Vertex, vertexCode, derivedVertex.WithPushDataCursor(derivedPixelStage.PushDataEnd), vertex.Code)
                is { } vertexDifference)
            {
                return $"graphics differs {name} {vertexDifference}";
            }

            return list.Contains(ShaderWorkList.StaticGraphicsIdentity(vertex.Identity, pixel.Identity, state.Identity))
                ? $"graphics same {name}"
                : $"graphics uncovered {name}";
        }

        private string? CompareDerivedStage(string stage, StageRecord recorded, ShaderCodeCapture recordedCode, StageRecord derived, ShaderCodeCapture derivedCode)
        {
            var prefix = stage.Length == 0 ? string.Empty : stage + ".";
            var left = GetCompiledStage(recorded, recordedCode);
            var right = GetCompiledStage(derived, derivedCode);
            if (left is null || right is null)
            {
                return $"{prefix}Translation {(left is null ? "failed" : "ok")} -> {(right is null ? "failed" : "ok")}";
            }

            if (left.Spirv.AsSpan().SequenceEqual(right.Spirv) && left.Bindings.AsSpan().SequenceEqual(right.Bindings) &&
                left.VertexFetchComponents.AsSpan().SequenceEqual(right.VertexFetchComponents) && left.PushDataEnd == right.PushDataEnd)
            {
                return null;
            }

            var difference = new RecordDifference()
                .Compare($"{prefix}Specialization",
                    ShaderProgramCache.EffectiveSpecialization(recorded, recordedCode),
                    ShaderProgramCache.EffectiveSpecialization(derived, derivedCode))
                .Compare($"{prefix}UserDataBase", recorded.UserDataBase, derived.UserDataBase)
                .Compare($"{prefix}UserDataCount", recorded.UserDataCount, derived.UserDataCount)
                .Compare($"{prefix}PushDataCursor", recorded.PushDataCursor, derived.PushDataCursor)
                .Compare($"{prefix}Compute", recorded.Compute, derived.Compute)
                .Compare($"{prefix}ComputeSystemRegisters", recorded.ComputeSystemRegisters, derived.ComputeSystemRegisters)
                .Compare($"{prefix}Vertex", recorded.Vertex, derived.Vertex)
                .Compare($"{prefix}Pixel", recorded.Pixel, derived.Pixel);
            return difference.Any ? difference.ToString() : $"{prefix}Spirv {left.Spirv.Length} -> {right.Spirv.Length}";
        }

        private static string ShaderCacheStampPart(Type type) => type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

        private static ulong ShaderCacheStamp(IGuestGpuBackend compiler, ShaderCompileHostFlags flags) =>
            XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(string.Join(
                '|',
                ShaderCacheStampPart(typeof(Gen5ShaderTranslator)),
                ShaderCacheStampPart(typeof(Gen5SpirvTranslator)),
                compiler.GetType().FullName,
                flags.ToString())));

        private CompiledStageRecord? GetCompiledStage(StageRecord record, ShaderCodeCapture code)
        {
            var identity = ShaderCacheFile.StageIdentity(record);
            return _warmStages.GetOrAdd(
                    identity,
                    _ => new Lazy<CompiledStageRecord?>(
                        () => TranslateStage(record, code),
                        LazyThreadSafetyMode.ExecutionAndPublication))
                .Value;
        }

        private CompiledStageRecord? TranslateStage(StageRecord record, ShaderCodeCapture code)
        {
            if (!ShaderProgramCache.TryReplay(record, code, GuestGpu.Current, _shaderCacheFlags, out var compiled, out var info, out var error))
            {
                NoteShaderCacheFailure(record.Stage.ToString(), record.Hash, error);
                return null;
            }

            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, info!, record.Stage);
            var pushDataEnd = record.PushDataCursor;
            info!.Bindings!.AdvancePushData(ref pushDataEnd);
            var stage = new CompiledStageRecord
            {
                Spirv = compiled!.Payload,
                Bindings = bindings
                    .Select(static binding => new LayoutBindingRecord(
                        binding.Binding, (int)binding.DescriptorType, binding.DescriptorCount, (uint)binding.StageFlags))
                    .ToArray(),
                VertexFetchComponents = info.VertexFetchComponents,
                PushDataEnd = pushDataEnd,
            };
            return stage;
        }

        private static List<DescriptorSetLayoutBinding> ToLayoutBindings(params CompiledStageRecord?[] stages)
        {
            var bindings = new List<DescriptorSetLayoutBinding>();
            foreach (var stage in stages)
            {
                if (stage is null)
                {
                    continue;
                }

                foreach (var binding in stage.Bindings)
                {
                    bindings.Add(new DescriptorSetLayoutBinding
                    {
                        Binding = binding.Binding,
                        DescriptorType = (DescriptorType)binding.DescriptorType,
                        DescriptorCount = binding.DescriptorCount,
                        StageFlags = (ShaderStageFlags)binding.StageFlags,
                    });
                }
            }

            return bindings;
        }

        private ShaderModule CreateWarmModule(byte[] spirv) => CreateShaderModule(spirv);

        private void DestroyWarmModule(ShaderModule module)
        {
            if (module.Handle == 0)
            {
                return;
            }

            _vk.DestroyShaderModule(_device, module, null);
        }

        private ShaderCacheOutcome CompileComputeItem(StageRecord record, ShaderCodeCapture code)
        {
            if (GetCompiledStage(record, code) is not { } stage)
            {
                return ShaderCacheOutcome.Failed;
            }

            var bindings = ToLayoutBindings(stage);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out _);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
            var module = CreateWarmModule(stage.Spirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var requireSubgroup32 = RequiresComputeSubgroup32(record.Compute!);
                var subgroup = RequiredSubgroupSize();
                var info = ComputeCreateInfo(module, layout, entryPoint, requireSubgroup32 ? &subgroup : null);
                if (HasStoredPipeline(&info))
                {
                    return ShaderCacheOutcome.FromStore;
                }

                _lastCaptureReachedDictionary = false;
                if (CreateCachedPipelineObject(false, &info, out var pipeline, out _, fast: false) != Result.Success)
                {
                    return ShaderCacheOutcome.Failed;
                }

                _vk.DestroyPipeline(_device, pipeline, null);
                return _lastCaptureReachedDictionary ? ShaderCacheOutcome.Dictionary : ShaderCacheOutcome.Compiled;
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                DestroyWarmModule(module);
                _vk.DestroyPipelineLayout(_device, layout, null);
                _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
            }
        }

        private ShaderCacheOutcome CompileGraphicsItem(
            GraphicsPipelineRecord record,
            ShaderCodeCapture vertexCode,
            ShaderCodeCapture? pixelCode,
            bool deriveVertexCursor)
        {
            CompiledStageRecord? pixel = null;
            if (record.Pixel is { } pixelRecord && (pixel = GetCompiledStage(pixelRecord, pixelCode!)) is null)
            {
                return ShaderCacheOutcome.Failed;
            }

            var vertexRecord = deriveVertexCursor && pixel is not null ? record.Vertex.WithPushDataCursor(pixel.PushDataEnd) : record.Vertex;
            if (GetCompiledStage(vertexRecord, vertexCode) is not { } vertex || record.Rendering.ColorCount > _maxColorAttachments)
            {
                return ShaderCacheOutcome.Failed;
            }

            var bindings = ToLayoutBindings(vertex, pixel);
            var pushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out _);
            var layout = CreatePipelineLayout(setLayout, pushStages);
            var vertexModule = CreateWarmModule(vertex.Spirv);
            var pixelModule = pixel is null ? default : CreateWarmModule(pixel.Spirv);
            try
            {
                var attributes = new VertexAttributeResource[record.Attributes.Length];
                for (var index = 0; index < attributes.Length; index++)
                {
                    var input = record.VertexInput.Attributes[index];
                    attributes[index] = new VertexAttributeResource(
                        record.Attributes[index].Descriptor, 0, record.Attributes[index].RegisterCount, index, 0, input.Binding, input.Offset);
                }

                var description = new GraphicsPipelineDescription
                {
                    Rendering = record.Rendering,
                    VertexInput = record.VertexInput,
                    VertexInfo = new VertexInputInfo { Attributes = attributes },
                    VertexProgram = new ShaderProgram(0, vertexModule.Handle),
                    VertexStage = new ShaderProgramInfo
                    {
                        Stage = ShaderStageKind.Vertex,
                        Hash = record.Vertex.Hash,
                        VertexFetchComponents = vertex.VertexFetchComponents,
                    },
                    PixelInfo = pixel is null ? null : new PixelInputInfo(),
                    PixelProgram = new ShaderProgram(0, pixelModule.Handle),
                    PixelStage = pixel is null ? null : new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Hash = record.Pixel!.Hash },
                    StaticParameters = record.StaticParameters,
                };
                var outcome = ShaderCacheOutcome.FromStore;
                foreach (var (topology, polygonMode) in PrecompileTopologies(record.StaticParameters.Topology))
                {
                    _lastCaptureReachedDictionary = false;
                    var pipeline = CreateRenderPipeline(description, topology, layout, polygonMode, precompile: true);
                    if (pipeline.Handle == 0)
                    {
                        continue;
                    }

                    _vk.DestroyPipeline(_device, pipeline, null);
                    if (_lastCaptureReachedDictionary)
                    {
                        outcome = ShaderCacheOutcome.Dictionary;
                    }
                    else if (outcome == ShaderCacheOutcome.FromStore)
                    {
                        outcome = ShaderCacheOutcome.Compiled;
                    }
                }

                return outcome;
            }
            finally
            {
                DestroyWarmModule(vertexModule);
                DestroyWarmModule(pixelModule);
                _vk.DestroyPipelineLayout(_device, layout, null);
                _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
            }
        }

        private IEnumerable<(PrimitiveTopology Topology, PolygonMode PolygonMode)> PrecompileTopologies(PrimitiveTopology topology)
        {
            if (topology != PrimitiveTopology.PatchList)
            {
                yield return (topology, PolygonMode.Fill);
                yield break;
            }

            yield return (PrimitiveTopology.TriangleStrip, PolygonMode.Fill);
            yield return (PrimitiveTopology.TriangleList, PolygonMode.Fill);
            if (_supportsFillRectangle)
            {
                yield return (PrimitiveTopology.TriangleList, PolygonMode.FillRectangleNV);
            }
        }

        private static PipelineShaderStageRequiredSubgroupSizeCreateInfo RequiredSubgroupSize() => new()
        {
            SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
            RequiredSubgroupSize = RdnaSubgroupSize,
        };

        private static ComputePipelineCreateInfo ComputeCreateInfo(
            ShaderModule module,
            PipelineLayout layout,
            byte* entryPoint,
            PipelineShaderStageRequiredSubgroupSizeCreateInfo* subgroup) => new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                PNext = subgroup,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = entryPoint,
            },
            Layout = layout,
        };

        private void NoteShaderCacheFailure(string kind, ulong identity, string error)
        {
            if (Interlocked.Increment(ref _shaderCacheFailed) <= MaxLoggedShaderCacheFailures)
            {
                Console.Error.WriteLine($"[SHADER CACHE] {kind} 0x{identity:X16} is skipped, the translator cannot compile it: {error}");
            }
        }

        private static string FormatCacheBytes(long bytes) =>
            string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB");

        private void StopShaderCache()
        {
            Interlocked.Exchange(ref _shaderCacheSessionTimer, null)?.Dispose();
            _shaderCacheCancel.Cancel();
            KillShaderCacheWorkers();
            _shaderCacheThread?.Join(TimeSpan.FromSeconds(30));
            SetShaderCacheState(false);
            if (Interlocked.Exchange(ref _shaderCache, null) is not { } file)
            {
                return;
            }

            ShaderInventory.Detach(file);
            UseShaderCacheStores(null);
            Console.Error.WriteLine(
                $"[SHADER CACHE] Session: pipelines from the store {Volatile.Read(ref _shaderCacheFromBinaries)}, " +
                $"compiled {Volatile.Read(ref _shaderCacheCompiled)}, rejected binaries {Volatile.Read(ref _shaderCacheBinaryRejected)}.");
            SaveShaderSeed(file, VideoOutExports.GetApplicationDisplayName());
            CompactShaderCache(file);
            file.Dispose();
        }

        private void CompactShaderCache(ShaderCacheFile file, bool dropUnreferencedBinaries = false)
        {
            if (Volatile.Read(ref _shaderCacheProgress) is not null)
            {
                UpdateShaderCacheProgress(ShaderCachePhase.Finalizing);
            }
            if (file.Compact(_shaderCacheStamp, _pipelineBinaries?.DriverKey ?? [], _shaderCacheDriverStamp,
                    dropUnreferencedBinaries: dropUnreferencedBinaries) is { } result)
            {
                Console.Error.WriteLine(
                    $"[SHADER CACHE] Dropped {result.RecordsDropped} stale records, " +
                    $"{FormatCacheBytes(result.BytesBefore)} -> {FormatCacheBytes(result.BytesAfter)}.");
            }
        }
    }
}
