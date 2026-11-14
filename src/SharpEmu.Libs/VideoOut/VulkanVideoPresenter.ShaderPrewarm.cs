// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
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
        private Thread[] _shaderPrewarmThreads = [];
        private PrewarmPool? _prewarmPool;
        private PrewarmPool PrewarmWorkers => _prewarmPool ??= new PrewarmPool(() => _shaderPrewarmStopping);
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
        private readonly ConcurrentQueue<ulong> _shaderPrewarmCompleted = new();
        // The stage programs of the whole pipelines the prewarm builds (StartPipelinePrewarm), each compiled once.
        private readonly ConcurrentDictionary<ulong, Lazy<PrewarmedStage?>> _prewarmStages = new();
        private readonly ConcurrentBag<ShaderModule> _prewarmStageModules = [];

        private sealed record PrewarmedStage(ShaderProgram Handle, ShaderProgramInfo Program);

        private readonly record struct PrewarmWork(ComputePrewarmRecord? Compute, ShaderCodeCapture? Code, PipelinePrewarmItem? Graphics);
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
            var stamp = list.CoverageStamp(buildStamp);
            StartGraphicsPrewarm(list, buildStamp, compiler);
            if (list.LoadedComputeCount == 0 && list.LoadedPipelineCount == 0)
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
                    $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} graphics={list.LoadedPipelineCount} " +
                    "reason=cache-and-recording-match");
                return false;
            }

            _loadedPrewarmCache = [];
            // Progress alone does not prove that the saved cache contains those pipelines.
            list.ClearProgress();
            var computes = list.LoadedComputes();
            var graphics = list.LoadedPipelines();
            Interlocked.Add(ref _shaderPrewarmFailed,
                list.LoadedComputeCount - computes.Count + list.LoadedPipelineCount - graphics.Count);
            var started = Stopwatch.GetTimestamp();
            var pendingCount = computes.Count + graphics.Count;
            if (pendingCount == 0)
            {
                FinishShaderPrewarm(list, stamp, started);
                return false;
            }

            var work = new ConcurrentQueue<PrewarmWork>(
                graphics.Select(static item => new PrewarmWork(null, null, item))
                    .Concat(computes.Select(static item => new PrewarmWork(item.Record, item.Code, null))));
            var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            var remaining = workerCount;
            var host = (IShaderPipelineHost)this;
            var sharedInt64Atomics = host.SharedInt64AtomicsEnabled;
            var execGuardElision = host.ExecGuardElisionEnabled;
            Volatile.Write(ref _shaderPrewarmTotal, pendingCount);
            Volatile.Write(ref _shaderPrewarmProgress, 0);
            Volatile.Write(ref _shaderPrewarmComputeTotal, computes.Count);
            Volatile.Write(ref _shaderPrewarmGraphicsTotal, graphics.Count);
            Volatile.Write(ref _shaderPrewarmWorkers, workerCount);
            _shaderPrewarmStarted = started;
            _shaderPrewarmProgressView = ShaderPrewarmProgressView;
            ShowShaderCacheProgress(_shaderPrewarmProgressView);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm started: compute={computes.Count} graphics={graphics.Count} resumed=0 workers={workerCount}");
            SetShaderPrewarmState(true);
            _shaderPrewarmThreads = new Thread[workerCount];
            for (var index = 0; index < workerCount; index++)
            {
                _shaderPrewarmThreads[index] = new Thread(() =>
                {
                    SubmissionScheduler.ThrowOnFatal = true;
                    while (!_shaderPrewarmStopping && work.TryDequeue(out var item))
                    {
                        if (item.Graphics is { } pipeline)
                        {
                            if (PrewarmGraphicsPipeline(pipeline, compiler))
                            {
                                _shaderPrewarmCompleted.Enqueue(pipeline.Identity);
                            }

                            Interlocked.Increment(ref _shaderPrewarmGraphicsDone);
                        }
                        else
                        {
                            if (PrewarmComputePipeline(item.Compute!, item.Code!, compiler, sharedInt64Atomics, execGuardElision))
                            {
                                _shaderPrewarmCompleted.Enqueue(ShaderPrewarmList.Identity(item.Compute!));
                            }

                            Interlocked.Increment(ref _shaderPrewarmComputeDone);
                        }

                        if (Interlocked.Increment(ref _shaderPrewarmProgress) % PrewarmProgressBatch == 0)
                        {
                            FlushShaderPrewarmProgress(list, stamp);
                        }
                    }

                    FlushShaderPrewarmProgress(list, stamp);
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        ReleasePrewarmStages();
                        FinishShaderPrewarm(list, stamp, started);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"SharpEmu shader prewarm {index}",
                };
                _shaderPrewarmThreads[index].Start();
            }

            return true;
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

        private void FlushShaderPrewarmProgress(ShaderPrewarmList list, string stamp)
        {
            var batch = new List<ulong>();
            while (_shaderPrewarmCompleted.TryDequeue(out var identity))
            {
                batch.Add(identity);
            }

            list.AppendProgress(stamp, batch);
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
            foreach (var thread in _shaderPrewarmThreads)
            {
                thread.Join(TimeSpan.FromSeconds(10));
            }

            _shaderPrewarmThreads = [];
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
