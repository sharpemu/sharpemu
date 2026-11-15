// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const int MaxLoggedPrewarmFailures = 8;
        private const int PrewarmProgressBatch = 256;

        private sealed record ShaderPrewarmCompletion(string StampPath, string CoverageStamp);
        private ShaderPrewarmCompletion? _shaderPrewarmCompletion;
        private byte[] _loadedPrewarmCache = [];

        private ShaderPrewarmList? _shaderPrewarmList;
        private ConcurrentDictionary<string, byte>? _prewarmedShaderIdentities;
        private ConcurrentDictionary<string, byte>? _runtimeComputeIdentities;
        private PrewarmPool? _prewarmPool;
        private volatile bool _shaderPrewarmStopping;
        private volatile bool _shaderPrewarmFinished;
        private int _shaderPrewarmCompiled;
        private int _shaderPrewarmFailed;
        private int _shaderPrewarmRuntimeHits;
        private int _shaderPrewarmDroppedLinked;
        // The background lane of the recorded linked permutations (StartLinkedPrewarm); teardown joins it.
        private Thread? _linkedPrewarmThread;
        // The splash's warm-up view: the items each kind has done and holds, every lane's threads, and the start.
        private int _shaderPrewarmComputeDone;
        private int _shaderPrewarmComputeTotal;
        private int _shaderPrewarmGraphicsDone;
        private int _shaderPrewarmGraphicsTotal;
        private int _shaderPrewarmWorkers;
        private long _shaderPrewarmStarted;
        private Func<ShaderCacheProgressView>? _shaderPrewarmProgressView;
        // The stage programs of the whole pipelines the prewarm builds (StartPipelinePrewarm), each compiled once.
        private readonly ConcurrentDictionary<ulong, Lazy<PrewarmedStage?>> _prewarmStages = new();
        private readonly ConcurrentBag<ShaderModule> _prewarmStageModules = [];

        private sealed record PrewarmedStage(ShaderProgram Handle, ShaderProgramInfo Program);

        // SHARPEMU_PREWARM_BINARY_SKIP (on by default; =0 turns it off): with pipeline binaries on, the graphics prewarm builds
        // only the items that no prewarm under the current build stamp completed yet (ShaderPrewarmList progress), and none when
        // every item is listed. Each listed item was built through CreatePipelineWithBinaries, which stored its binaries when the
        // build took the store threshold (PipelineBinaryStorePolicy) or longer, and the session's own creation loads them. A
        // session records a few new items each time, so the next prewarm builds those alone. The progress is keyed by the build
        // stamp alone (the translator and compiler assemblies, the device and the translation switches), not by the coverage
        // stamp, which also hashes the recorded list and so changes with every session. A new build prewarms every item once.
        // The compute prewarm does not skip this way: the translation cache serves its translations and the session takes the
        // pipelines it builds, so it runs every session unless CanSkipShaderPrewarm certifies the whole cache (a list without
        // linked records whose cache and recording stamps match); skipping it otherwise moves its pipelines to first use.
        private static readonly bool PrewarmBinarySkip = Environment.GetEnvironmentVariable("SHARPEMU_PREWARM_BINARY_SKIP") != "0";

        // A linked permutation can be a whole-module compile of many minutes and gigabytes of driver memory, so the prewarm the
        // game waits for leaves the recorded ones out. By default they compile on a background thread the game does not wait
        // for (StartLinkedPrewarm); SHARPEMU_PREWARM_LINKED=all compiles them with the rest of the prewarm, =none not at all.
        private static readonly string PrewarmLinked = Environment.GetEnvironmentVariable("SHARPEMU_PREWARM_LINKED") switch
        {
            null or "" or "background" => "background",
            "none" => "none",
            _ => "all",
        };

        ShaderPrewarmList? IShaderPipelineHost.ShaderPrewarm => Volatile.Read(ref _shaderPrewarmList);

        private void StartShaderPrewarm()
        {
            if (!StartShaderPrewarmCore())
            {
                SetShaderPrewarmState(false);
            }

            // The pool's threads begin once every lane is in.
            _prewarmPool?.Start();
        }

        private bool StartShaderPrewarmCore()
        {
            if (_shaderPrewarmList is not null ||
                _pipelineCacheShardDirectory is not null ||
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM") == "0")
            {
                _loadedPrewarmCache = [];
                return false;
            }

            var directory = Path.GetDirectoryName(VulkanPipelineCacheStorage.ResolvePath(
                VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")));
            if (string.IsNullOrEmpty(directory) || ShaderPrewarmList.Open(directory) is not { } list)
            {
                _loadedPrewarmCache = [];
                return false;
            }

            _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            Volatile.Write(ref _shaderPrewarmList, list);
            var compiler = GuestGpu.Current;
            var buildStamp = ShaderPrewarmStamp(compiler);
            // Whole pipelines serve a device that creates whole pipelines. With pipeline libraries the session links the parts
            // the graphics prewarm stores (StartGraphicsPrewarm) and would never take a whole pipeline built here.
            var pipelines = !_graphicsPipelineLibraries;
            var stamp = list.CoverageStamp(buildStamp, pipelines);
            StartGraphicsPrewarm(list, buildStamp, compiler);
            var pipelineCount = pipelines ? list.LoadedPipelineCount : 0;
            if (list.LoadedComputeCount == 0 && pipelineCount == 0)
            {
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                return false;
            }

            if (CanSkipShaderPrewarm(list, stamp, _loadedPrewarmCache))
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} graphics={pipelineCount} " +
                    "reason=cache-and-recording-match");
                return false;
            }

            _loadedPrewarmCache = [];
            IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code, float StartBy)> pending = list.LoadedComputeStarts();
            var missingCode = list.LoadedComputeCount - pending.Count;
            if (PrewarmLinked != "all")
            {
                var plain = pending.Where(static item => item.Record.Linked is null).ToList();
                if (PrewarmLinked == "background")
                {
                    StartLinkedPrewarm(list, [.. pending.Where(static item => item.Record.Linked is not null)
                        .Select(static item => (item.Record, item.Code))], compiler);
                }
                else
                {
                    Volatile.Write(ref _shaderPrewarmDroppedLinked, pending.Count - plain.Count);
                }

                pending = plain;
            }

            IReadOnlyList<PipelinePrewarmItem> graphics = pipelines ? list.LoadedPipelines() : [];
            Interlocked.Add(ref _shaderPrewarmFailed, missingCode + pipelineCount - graphics.Count);
            var started = Stopwatch.GetTimestamp();
            if (pending.Count == 0 && graphics.Count == 0)
            {
                FinishShaderPrewarm(list, stamp, started);
                return false;
            }

            var work = new ShaderPrewarmQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code, float StartBy)>(pending,
                static item => item.Record.Linked is null ? null : item.Record.Hash);
            var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            var host = (IShaderPipelineHost)this;
            var sharedInt64Atomics = host.SharedInt64AtomicsEnabled;
            var execGuardElision = host.ExecGuardElisionEnabled;
            // The game waits until the compute records and the whole pipelines are done: the last lane to end finishes.
            var lanes = (pending.Count != 0 ? 1 : 0) + (graphics.Count != 0 ? 1 : 0);
            void FinishLane()
            {
                if (Interlocked.Decrement(ref lanes) == 0)
                {
                    FinishShaderPrewarm(list, stamp, started);
                }
            }

            Volatile.Write(ref _shaderPrewarmTotal, pending.Count + graphics.Count);
            Volatile.Write(ref _shaderPrewarmProgress, 0);
            Volatile.Write(ref _shaderPrewarmComputeTotal, pending.Count);
            Interlocked.Add(ref _shaderPrewarmGraphicsTotal, graphics.Count);
            Interlocked.Add(ref _shaderPrewarmWorkers, pending.Count != 0 ? workerCount : 0);
            _shaderPrewarmStarted = started;
            _shaderPrewarmProgressView = ShaderPrewarmProgressView;
            ShowShaderCacheProgress(_shaderPrewarmProgressView);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm started: compute={pending.Count} graphics={graphics.Count} workers={workerCount} " +
                $"dropped_linked={Volatile.Read(ref _shaderPrewarmDroppedLinked)}");
            SetShaderPrewarmState(true);
            if (pending.Count != 0)
            {
                PrewarmWorkers.Add(new PrewarmPool.Lane(pending.Count,
                    (out float start) =>
                    {
                        var any = work.TryPeek(out var next);
                        start = any ? next.StartBy : 0;
                        return any;
                    },
                    mayWait =>
                    {
                        if (!work.TryTake(() => _shaderPrewarmStopping, out var item, out var gate, mayWait))
                        {
                            return false;
                        }

                        bool compiled;
                        var began = Stopwatch.GetTimestamp();
                        try
                        {
                            compiled = PrewarmComputePipeline(item.Record, item.Code, compiler, sharedInt64Atomics, execGuardElision);
                        }
                        finally
                        {
                            work.Release(gate);
                        }

                        if (compiled)
                        {
                            list.RecordComputeCost(item.Record, (float)Stopwatch.GetElapsedTime(began).TotalSeconds);
                        }

                        Interlocked.Increment(ref _shaderPrewarmComputeDone);
                        Interlocked.Increment(ref _shaderPrewarmProgress);
                        return true;
                    },
                    FinishLane),
                    workerCount, "SharpEmu shader prewarm");
            }

            if (graphics.Count != 0)
            {
                StartPipelinePrewarm(graphics, compiler, FinishLane);
            }

            return true;
        }

        // The compute and graphics prewarms share one pool of below-normal threads.
        private PrewarmPool PrewarmWorkers => _prewarmPool ??= new PrewarmPool(() => _shaderPrewarmStopping);

        // The whole pipelines sessions created (RecordGraphics), built again with their stage programs. Each one goes to the
        // device pipeline cache, where the session's creation of the same pipeline finds the driver's compile; the stage
        // programs and their modules go when the lane ends.
        private void StartPipelinePrewarm(IReadOnlyList<PipelinePrewarmItem> items, IGuestGpuBackend compiler, Action finished)
        {
            var work = new ConcurrentQueue<PipelinePrewarmItem>(items);
            var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            Interlocked.Add(ref _shaderPrewarmWorkers, workerCount);
            PrewarmWorkers.Add(new PrewarmPool.Lane(items.Count,
                (out float start) =>
                {
                    // Sessions do not time a whole pipeline's first use: it is due as an untimed compute record is (StartBy).
                    start = float.PositiveInfinity;
                    return !work.IsEmpty;
                },
                _ =>
                {
                    if (!work.TryDequeue(out var item))
                    {
                        return false;
                    }

                    PrewarmGraphicsPipeline(item, compiler);
                    Interlocked.Increment(ref _shaderPrewarmGraphicsDone);
                    Interlocked.Increment(ref _shaderPrewarmProgress);
                    return true;
                },
                () =>
                {
                    ReleasePrewarmStages();
                    finished();
                }),
                workerCount, "SharpEmu pipeline prewarm");
        }

        // The linked permutations earlier sessions recorded, compiled on below-normal threads the game does not wait for. The
        // driver can spend minutes on one, and a compile the session starts at the caller's first dispatch can still be
        // running when the session ends: then nothing is stored, and every session skips the caller's dispatches as long.
        // Started at boot, a permutation is stored when it finishes (its pipeline binaries, or the caller's linked cache), and
        // the session's request for the same module waits for that compile and loads its result instead of compiling it
        // beside it (the pipeline build gate and LinkedCompileAdmission). Each compile waits for its admission like the
        // session's own. The order is ShaderPrewarmQueue.LinkedLaneOrder: each caller's first record is the one its session
        // asks for first, since a session that asks for another large module waits for the lane's compile and then compiles
        // its own. With cores to spare a second worker takes the next record whose caller the first is not compiling (the
        // permutations of one caller share its linked cache file), so callers that compile in a minute or two are ready
        // before the session reaches them instead of waiting behind a caller that compiles for several minutes; while they
        // wait, the session skips their dispatches and draws without their results.
        private void StartLinkedPrewarm(ShaderPrewarmList list,
            IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code)> records, IGuestGpuBackend compiler)
        {
            if (records.Count == 0)
            {
                return;
            }

            var host = (IShaderPipelineHost)this;
            var sharedInt64Atomics = host.SharedInt64AtomicsEnabled;
            var execGuardElision = host.ExecGuardElisionEnabled;
            var workers = ShaderPrewarmQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code)>.LinkedLaneWorkers(
                Environment.ProcessorCount);
            var thread = new Thread(() =>
            {
                var started = Stopwatch.GetTimestamp();
                // Read here, off the boot path: the store decodes every kept callee.
                var keptCallees = ShaderCallUnionStore.Open();
                var firstAsked = records.Where(item => item.Record.Linked is { } linked &&
                        keptCallees.KeepsCalleesOf(ShaderStage.Compute, item.Record.Hash, linked))
                    .Select(static item => item.Record).ToHashSet<ComputePrewarmRecord>(ReferenceEqualityComparer.Instance);
                var ordered = ShaderPrewarmQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code)>.LinkedLaneOrder(
                    records, static item => item.Record.Hash, list.IsSlowLinkedCompile, item => firstAsked.Contains(item.Record));
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Linked prewarm started: permutations={ordered.Length} workers={workers} callers=" +
                    string.Join(',', ordered.GroupBy(static item => item.Record.Hash).Select(group =>
                        $"0x{group.Key:X16}x{group.Count()}{(list.IsSlowLinkedCompile(group.Key) ? "(slow)" : "")}" +
                        $"{(group.Any(item => firstAsked.Contains(item.Record)) ? "(kept)" : "")}")));
                var queue = new ShaderPrewarmQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code)>(
                    ordered, static item => item.Record.Hash, keepOrder: true);
                var compiled = 0;
                void Work()
                {
                    while (queue.TryTake(() => _shaderPrewarmStopping, out var item, out var gate))
                    {
                        try
                        {
                            var (record, code) = item;
                            var began = Stopwatch.GetTimestamp();
                            if (!PrewarmComputePipelineCore(record, code, compiler, sharedInt64Atomics, execGuardElision,
                                    waitForAdmission: true))
                            {
                                continue;
                            }

                            Interlocked.Increment(ref compiled);
                            var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
                            list.RecordComputeCost(record, (float)seconds);
                            Console.Error.WriteLine(
                                $"[LOADER][INFO] Linked prewarm compiled: hash=0x{record.Hash:X16} " +
                                $"callees={record.Linked?.Functions.Length ?? 0} seconds={seconds:F1}");
                        }
                        finally
                        {
                            queue.Release(gate);
                        }
                    }
                }

                var helpers = new Thread[workers - 1];
                for (var index = 0; index < helpers.Length; index++)
                {
                    helpers[index] = new Thread(Work)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = $"SharpEmu linked prewarm {index + 2}",
                    };
                    helpers[index].Start();
                }

                Work();
                foreach (var helper in helpers)
                {
                    helper.Join();
                }

                Console.Error.WriteLine(
                    $"[LOADER][INFO] Linked prewarm {(_shaderPrewarmStopping ? "stopped" : "done")}: compiled={Volatile.Read(ref compiled)} " +
                    $"of {ordered.Length} seconds={Stopwatch.GetElapsedTime(started).TotalSeconds:F0}");
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "SharpEmu linked prewarm",
            };
            _linkedPrewarmThread = thread;
            thread.Start();
        }

        private ShaderCacheProgressView ShaderPrewarmProgressView() => new(
            "Warming up shaders",
            "Rebuilding pipelines recorded in earlier sessions",
            [
                new ShaderCacheProgressRow("Compute pipelines",
                    Volatile.Read(ref _shaderPrewarmComputeDone), Volatile.Read(ref _shaderPrewarmComputeTotal)),
                new ShaderCacheProgressRow("Graphics pipelines",
                    Volatile.Read(ref _shaderPrewarmGraphicsDone), Volatile.Read(ref _shaderPrewarmGraphicsTotal)),
            ],
            [
                $"Warmed {Volatile.Read(ref _shaderPrewarmCompiled) + Volatile.Read(ref _graphicsPrewarmCompiled)} / " +
                $"Failed {Volatile.Read(ref _shaderPrewarmFailed) + Volatile.Read(ref _graphicsPrewarmFailed)}",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{Volatile.Read(ref _shaderPrewarmWorkers)} workers / {Stopwatch.GetElapsedTime(_shaderPrewarmStarted).TotalSeconds:F0} seconds"),
            ],
            Stopwatch.GetElapsedTime(_shaderPrewarmStarted).TotalSeconds);

        private void EndShaderPrewarmProgress()
        {
            if (Interlocked.Exchange(ref _shaderPrewarmProgressView, null) is { } view)
            {
                HideShaderCacheProgress(view);
            }
        }

        private static bool CanSkipShaderPrewarm(ShaderPrewarmList list, string stamp, byte[] cache) =>
            cache.Length != 0 && !list.HasLinkedComputes &&
            list.IsStampCurrent(ShaderPrewarmList.CacheStamp(stamp, cache));

        private static string ShaderPrewarmStampPart(Type type) =>
            type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

        private string ShaderPrewarmStamp(IGuestGpuBackend compiler)
        {
            var stamp = BuildShaderPrewarmStamp(compiler.GetType(), DriverCacheSignature());
            var frontends = Agc.AgcExports.ShaderFrontendStamp();
            return frontends.Length == 0 ? stamp : $"{stamp}|frontends-v1|{frontends}";
        }

        private string BuildShaderPrewarmStamp(Type compilerType, string driverSignature) =>
            string.Join(
                '|',
                "success-only-v2",
                ShaderPrewarmStampPart(typeof(ShaderProgramCache)),
                ShaderPrewarmStampPart(typeof(Gen5ShaderTranslator)),
                ShaderPrewarmStampPart(typeof(Gen5SpirvTranslator)),
                ShaderPrewarmStampPart(compilerType),
                compilerType.FullName,
                ((IShaderPipelineHost)this).SharedInt64AtomicsEnabled,
                ((IShaderPipelineHost)this).ExecGuardElisionEnabled,
                _supportsShaderSignedZeroInfNanPreserveFloat32,
                _nativeFloat16Conversions,
                SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                Volatile.Read(ref _nativeSubgroupSize),
                _canRequireComputeSubgroup32,
                _canRequireComputeSubgroup64,
                _maxComputeWorkgroupSubgroups,
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_HALF_MASKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_LANE_LOCAL"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_PAIRED"),
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_GUARD_ELISION"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_SELECT_ELIMINATION"),
                Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_EXEC_GUARDS"),
                Environment.GetEnvironmentVariable("SHARPEMU_PREWARM_LINKED"),
                driverSignature.TrimEnd('\n'));

        // Different permutations of one caller write the same linked cache file: the prewarm's queue (ShaderPrewarmQueue)
        // gives each caller's records to one worker at a time.
        private bool PrewarmComputePipeline(
            ComputePrewarmRecord record,
            ShaderCodeCapture code,
            IGuestGpuBackend compiler,
            bool sharedInt64Atomics,
            bool execGuardElision) =>
            PrewarmComputePipelineCore(record, code, compiler, sharedInt64Atomics, execGuardElision, waitForAdmission: false);

        private bool PrewarmComputePipelineCore(
            ComputePrewarmRecord record,
            ShaderCodeCapture code,
            IGuestGpuBackend compiler,
            bool sharedInt64Atomics,
            bool execGuardElision,
            bool waitForAdmission)
        {
            if (!ShaderProgramCache.TryCompilePrewarm(
                    record, code, compiler, sharedInt64Atomics, execGuardElision,
                    _supportsShaderSignedZeroInfNanPreserveFloat32, out var compiled, out var layout, out var error,
                    hostWave64Supported: ((IShaderPipelineHost)this).ComputeWave64SubgroupNative,
                    hostComputeSubgroup32: ((IShaderPipelineHost)this).ComputeRunsOn32LaneSubgroups(record.Info),
                    imageInt64Atomics: ImageInt64AtomicsEnabled,
                    shaderDeviceClock: ShaderDeviceClockEnabled,
                    bindlessHeap: _bindlessHeapSupported,
                    stableImageLimits: ((IShaderPipelineHost)this).ComputeImageDescriptorLimits,
                    bufferInt64Atomics: BufferInt64AtomicsEnabled,
                    robustBufferAccess2: _robustBufferAccess2Enabled,
                    nativeFloat16Conversions: _nativeFloat16Conversions,
                    frontendOf: Agc.AgcExports.ShaderFrontendForDomain))
            {
                NoteShaderPrewarmFailure("cs", record.Hash, record.Address, error);
                return false;
            }

            var payload = compiled!.Payload;
            var bindings = new List<DescriptorSetLayoutBinding>();
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            ShaderModule module = default;
            Pipeline pipeline = default;
            DriverCacheShard? linkedCache = null;
            var created = false;
            // A linked record compiles only once the admission of the session's linked compiles (LinkedCompileAdmission)
            // admits it, so the prewarm's linked compiles keep to the same commit budget. The prewarm the game waits for
            // takes only an admission given at once, so none of its threads waits behind the session's, and drops the record
            // otherwise; the background lane (StartLinkedPrewarm) waits for it. The try that releases the admission follows
            // at once.
            var linked = layout!.CallTargetCount != 0;
            if (linked && !WaitForLinkedAdmission(record.Hash, payload.Length, mayWait: waitForAdmission))
            {
                Interlocked.Increment(ref _shaderPrewarmDroppedLinked);
                return false;
            }

            try
            {
                // An admission can come after teardown began; a compile that cannot be cancelled must not start then.
                if (linked && _shaderPrewarmStopping)
                {
                    return false;
                }

                CollectLayoutBindings(bindings, layout, ShaderStage.Compute);
                setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out _);
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit, layout.UsesBindlessHeap);
                module = CreateShaderModule(payload);
                linkedCache = linked ? CreateLinkedPipelineCache(record.Hash) : null;
                var cache = ResolveGuestPipelineCache(linkedCache?.Cache);
                var identity = VulkanPipelineCacheStorage.CompiledShaderIdentity(payload);
                var requiredSubgroupSize = ComputeRequiredSubgroupSize(record.Info);
                pipeline = CompileComputePipeline(_vk, _device, cache, module, pipelineLayout, requiredSubgroupSize);
                created = true;
                // A plain record's pipeline waits, with the layouts it was compiled with, for the session's request (a
                // linked one goes: the session links the callees its own program kept). The handover owns what it took;
                // finally destroys the rest.
                if (!linked && _prewarmedComputePipelines.TryAdd(
                        ComputeHandoverKey(identity, SetSignature(bindings, usesPushDescriptors), layout.UsesBindlessHeap,
                            requiredSubgroupSize),
                        new PrewarmedComputePipeline(pipeline, setLayout, pipelineLayout)))
                {
                    pipeline = default;
                    setLayout = default;
                    pipelineLayout = default;
                }

                _prewarmedShaderIdentities![identity] = 0;
                Interlocked.Increment(ref _shaderPrewarmCompiled);
                return true;
            }
            catch (Exception exception)
            {
                NoteShaderPrewarmFailure("cs", record.Hash, record.Address, exception.Message);
                return false;
            }
            finally
            {
                if (linked)
                {
                    ReleaseLinkedAdmission(payload.Length);
                }

                if (pipeline.Handle != 0)
                {
                    _vk.DestroyPipeline(_device, pipeline, null);
                }

                ReleaseLinkedPipelineCache(linkedCache, record.Hash, save: created);
                if (module.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, module, null);
                }

                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (setLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                }
            }
        }

        // The compute prewarm's pipelines, each with the layouts it was compiled with, until the session asks for one
        // (TakePrewarmedComputePipeline). The key holds the SHA-256 of the module's SPIR-V, the set layout, whether the layout
        // binds the bindless heap and the required subgroup size: everything the pipeline was created from. Teardown destroys
        // the ones never asked for (DestroyPrewarmedComputePipelines).
        private readonly record struct PrewarmedComputePipeline(Pipeline Pipeline, DescriptorSetLayout SetLayout, PipelineLayout Layout);

        private readonly ConcurrentDictionary<string, PrewarmedComputePipeline> _prewarmedComputePipelines =
            new(StringComparer.Ordinal);
        private int _prewarmedComputeTaken;

        private static string ComputeHandoverKey(string identity, string setSignature, bool bindlessHeap, uint requiredSubgroupSize) =>
            $"{identity}|{setSignature}|{bindlessHeap}|{requiredSubgroupSize}";

        // The prewarm's pipeline for this module, set layout, heap and subgroup size, if it built one.
        private bool TakePrewarmedComputePipeline(ulong module, List<DescriptorSetLayoutBinding> bindings, bool usesPushDescriptors,
            bool bindlessHeap, uint requiredSubgroupSize, out PrewarmedComputePipeline prewarmed)
        {
            prewarmed = default;
            if (!_shaderModuleCacheIdentities.TryGetValue(module, out var identity) || !_prewarmedComputePipelines.TryRemove(
                    ComputeHandoverKey(identity, SetSignature(bindings, usesPushDescriptors), bindlessHeap, requiredSubgroupSize),
                    out prewarmed))
            {
                return false;
            }

            Interlocked.Increment(ref _prewarmedComputeTaken);
            return true;
        }

        private void DestroyPrewarmedComputePipeline(PrewarmedComputePipeline prewarmed)
        {
            _vk.DestroyPipeline(_device, prewarmed.Pipeline, null);
            _vk.DestroyPipelineLayout(_device, prewarmed.Layout, null);
            _vk.DestroyDescriptorSetLayout(_device, prewarmed.SetLayout, null);
        }

        private void DestroyPrewarmedComputePipelines()
        {
            var unclaimed = 0;
            foreach (var key in _prewarmedComputePipelines.Keys)
            {
                if (_prewarmedComputePipelines.TryRemove(key, out var prewarmed))
                {
                    DestroyPrewarmedComputePipeline(prewarmed);
                    unclaimed++;
                }
            }

            var taken = Volatile.Read(ref _prewarmedComputeTaken);
            if (taken + unclaimed != 0)
            {
                Console.Error.WriteLine($"[LOADER][INFO] Prewarmed compute pipelines: taken={taken} unclaimed={unclaimed}");
            }
        }

        private bool PrewarmGraphicsPipeline(PipelinePrewarmItem item, IGuestGpuBackend compiler)
        {
            var vertex = PrewarmStageProgram(item.Vertex, compiler);
            var pixel = item.Pixel is { } pixelSource ? PrewarmStageProgram(pixelSource, compiler) : null;
            if (vertex is null || (item.Pixel is not null && pixel is null))
            {
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }

            var record = item.Record;
            var rectangleList = record.StaticParameters.Topology == PrimitiveTopology.PatchList;
            if (rectangleList && !_supportsFillRectangle)
            {
                return true;
            }

            var description = new GraphicsPipelineDescription
            {
                Rendering = record.Rendering,
                VertexInput = record.VertexInput,
                VertexInfo = record.VertexInfo,
                VertexProgram = vertex.Handle,
                VertexStage = vertex.Program,
                PixelInfo = item.Pixel?.Record.Options.PixelInfo,
                PixelProgram = pixel?.Handle ?? default,
                PixelStage = pixel?.Program,
                StaticParameters = record.StaticParameters,
            };
            var bindings = new List<DescriptorSetLayoutBinding>();
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            try
            {
                CollectLayoutBindings(bindings, description.VertexStage, ShaderStage.Vertex);
                if (description.PixelStage is { } pixelStage)
                {
                    CollectLayoutBindings(bindings, pixelStage, ShaderStage.Pixel);
                }

                setLayout = CreateDescriptorSetLayout(bindings, out _, out _);
                var bindlessHeap = UsesBindlessHeap(description.VertexStage) ||
                    (description.PixelStage is { } heapPixelStage && UsesBindlessHeap(heapPixelStage));
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, bindlessHeap);
                var pipeline = rectangleList
                    ? CreateRenderPipeline(description, PrimitiveTopology.TriangleList, pipelineLayout, PolygonMode.FillRectangleNV, prewarm: true)
                    : CreateRenderPipeline(description, record.StaticParameters.Topology, pipelineLayout, prewarm: true);
                _vk.DestroyPipeline(_device, pipeline, null);
                Interlocked.Increment(ref _shaderPrewarmCompiled);
                return true;
            }
            catch (Exception exception)
            {
                NoteShaderPrewarmFailure("vs", item.Vertex.Record.Hash, item.Vertex.Record.Registered.CodeAddress, exception.Message);
                return false;
            }
            finally
            {
                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (setLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                }
            }
        }

        private PrewarmedStage? PrewarmStageProgram(PrewarmStage stage, IGuestGpuBackend compiler) =>
            _prewarmStages.GetOrAdd(stage.Identity, _ => new Lazy<PrewarmedStage?>(() => CompilePrewarmStage(stage, compiler))).Value;

        private PrewarmedStage? CompilePrewarmStage(PrewarmStage stage, IGuestGpuBackend compiler)
        {
            var record = stage.Record;
            var code = stage.Code;
            var label = record.Stage == ShaderStage.Vertex ? "vs" : "ps";
            try
            {
                IShaderFrontend? frontend = null;
                if (code.FrontendDomain != 0 && (frontend = Agc.AgcExports.ShaderFrontendForDomain(code.FrontendDomain)) is null)
                {
                    NoteShaderPrewarmFailure(label, record.Hash, record.Registered.CodeAddress,
                        $"The shader frontend 0x{code.FrontendDomain:X16} is not registered.");
                    return null;
                }

                var registered = record.Registered with { Frontend = frontend, FrontendUserData = code.FrontendUserData };
                var source = new ShaderSource(registered, record.Hash, new uint[record.UserDataCount], record.UserDataBase, record.Stage);
                var programs = new ShaderProgramCache(code.CreateContext(), compiler, new PrewarmShaderHost(this));
                var (handle, program) = programs.CompilePrewarmStage(source, record.Options, record.Specialization, record.PushDataCursor);
                return new PrewarmedStage(handle, program);
            }
            catch (Exception exception)
            {
                NoteShaderPrewarmFailure(label, record.Hash, record.Registered.CodeAddress, exception.Message);
                return null;
            }
        }

        private void ReleasePrewarmStages()
        {
            _prewarmStages.Clear();
            while (_prewarmStageModules.TryTake(out var module))
            {
                _vk.DestroyShaderModule(_device, module, null);
            }
        }

        private void NoteShaderPrewarmFailure(string kind, ulong hash, ulong address, string error)
        {
            if (Interlocked.Increment(ref _shaderPrewarmFailed) <= MaxLoggedPrewarmFailures)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Shader prewarm failed: {kind}=0x{hash:X16} shader=0x{address:X16} error={error}");
            }
        }

        // The stage programs of a whole pipeline compile as a session compiles them, with the device's capabilities.
        private sealed class PrewarmShaderHost(Presenter presenter) : IShaderPipelineHost
        {
            private readonly IShaderPipelineHost _inner = presenter;

            public uint MaxPushDescriptors => _inner.MaxPushDescriptors;
            public StableImageDescriptorLimits ComputeImageDescriptorLimits => _inner.ComputeImageDescriptorLimits;
            public StableImageDescriptorLimits GraphicsImageDescriptorLimits => _inner.GraphicsImageDescriptorLimits;
            public bool ComputeWave64Supported => _inner.ComputeWave64Supported;
            public bool ComputeWave64SubgroupNative => _inner.ComputeWave64SubgroupNative;
            public bool ComputeRunsOn32LaneSubgroups(ComputeInputInfo input) => _inner.ComputeRunsOn32LaneSubgroups(input);
            public bool MipStatisticsSubgroupReduction => _inner.MipStatisticsSubgroupReduction;
            public bool PixelSubgroupArithmetic => _inner.PixelSubgroupArithmetic;
            public bool RuntimeCallRecordsSupported => _inner.RuntimeCallRecordsSupported;
            public bool CallMarksSupported => _inner.CallMarksSupported;
            public bool TableBlocks => _inner.TableBlocks;
            public bool GraphicsSubgroupOperationsEnabled => _inner.GraphicsSubgroupOperationsEnabled;
            public bool SharedInt64AtomicsEnabled => _inner.SharedInt64AtomicsEnabled;
            public bool BufferInt64AtomicsEnabled => _inner.BufferInt64AtomicsEnabled;
            public bool ImageInt64AtomicsEnabled => _inner.ImageInt64AtomicsEnabled;
            public bool ShaderDeviceClockEnabled => _inner.ShaderDeviceClockEnabled;
            public bool ShaderSignedZeroInfNanPreserveFloat32Supported => _inner.ShaderSignedZeroInfNanPreserveFloat32Supported;
            public bool RobustBufferAccess2Enabled => _inner.RobustBufferAccess2Enabled;
            public bool NativeFloat16Conversions => _inner.NativeFloat16Conversions;
            public bool ExecGuardElisionEnabled => _inner.ExecGuardElisionEnabled;
            public bool PerVertexPixelInputsSupported => _inner.PerVertexPixelInputsSupported;
            public bool ClipDistanceEnabled => _inner.ClipDistanceEnabled;
            public bool ShaderDrawParametersSupported => _inner.ShaderDrawParametersSupported;
            public bool MeshShadersSupported => _inner.MeshShadersSupported;
            public MeshShaderLimits MeshLimits => _inner.MeshLimits;
            public uint MeshSubgroupSize => _inner.MeshSubgroupSize;
            public RenderHostLimits Limits => _inner.Limits;
            public bool SupportsDualSourceBlend => _inner.SupportsDualSourceBlend;
            public bool SupportsBindlessHeap => _inner.SupportsBindlessHeap;
            public SampleCountFlags NoAttachmentSampleCounts => _inner.NoAttachmentSampleCounts;

            public bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap, out Gen5PixelOutputKind outputKind,
                out Gen5ColorComponentMapping componentMapping) =>
                _inner.TryResolveColorOutput(dataFormat, numberType, componentSwap, out outputKind, out componentMapping);

            public bool TryReadGuestWord(ulong address, out uint word)
            {
                word = 0;
                return false;
            }

            public bool TryReadCleanGuestWord(ulong address, out uint word)
            {
                word = 0;
                return false;
            }

            public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
            {
                var module = presenter.CreateShaderModule(shader.Payload);
                presenter._prewarmStageModules.Add(module);
                return module.Handle;
            }

            public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description) =>
                throw new NotSupportedException("The prewarm host does not create pipelines.");

            public PipelineHandle CreateComputePipeline(ComputePipelineDescription description) =>
                throw new NotSupportedException("The prewarm host does not create pipelines.");
        }

        private void FinishShaderPrewarm(ShaderPrewarmList list, string stamp, long started)
        {
            var runtime = _runtimeComputeIdentities!;
            var prewarmed = _prewarmedShaderIdentities!;
            var matched = runtime.Keys.Count(prewarmed.ContainsKey);
            EndShaderPrewarmProgress();
            if (_shaderPrewarmStopping)
            {
                SetShaderPrewarmState(false);
                return;
            }

            if (Volatile.Read(ref _shaderPrewarmFailed) == 0)
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
            }
            _shaderPrewarmFinished = true;
            SetShaderPrewarmState(false);
            _pipelineCacheDirty = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm done: compiled={Volatile.Read(ref _shaderPrewarmCompiled)} " +
                $"failed={Volatile.Read(ref _shaderPrewarmFailed)} dropped_linked={Volatile.Read(ref _shaderPrewarmDroppedLinked)} " +
                $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
                $"runtime_compute_modules={runtime.Count} matched={matched}");
        }

        private void NoteRuntimeComputeModule(string identity)
        {
            if (_runtimeComputeIdentities is not { } runtime || _prewarmedShaderIdentities is not { } prewarmed)
            {
                return;
            }

            runtime[identity] = 0;
            if (!_shaderPrewarmFinished || !prewarmed.ContainsKey(identity))
            {
                return;
            }

            var hits = Interlocked.Increment(ref _shaderPrewarmRuntimeHits);
            if (hits is 1 or 10 or 100 or 1000)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm hits: {hits} of runtime_compute_modules={runtime.Count}");
            }
        }

        private void StopShaderPrewarm()
        {
            _shaderPrewarmStopping = true;
            EndShaderPrewarmProgress();
            SetShaderPrewarmState(false);
            // A worker stops after its current item. Its pipeline create cannot be cancelled and teardown destroys the
            // device next, so each one is joined without a bound, as DrainPendingComputePipelines waits for the session's
            // own compiles.
            _prewarmPool?.Stop();
            _prewarmPool = null;
            // The linked lane stops after its current permutation in the same way; one still waiting for its admission gives
            // up, as teardown closed the admission first.
            Interlocked.Exchange(ref _linkedPrewarmThread, null)?.Join();
            DestroyPrewarmedComputePipelines();
            if (Interlocked.Exchange(ref _shaderPrewarmList, null) is { } list)
            {
                list.Dispose();
            }
        }
    }
}
