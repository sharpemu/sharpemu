// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // Pipelines that take time to build are kept as pipeline binaries (VK_KHR_pipeline_binary), one file per driver
        // pipeline key. The driver's own shader cache does not keep them: a pipeline that captures its data is built
        // without a pipeline cache, and NVIDIA's cache has a size limit besides (1 GiB for every Vulkan and OpenGL program
        // together, storing nothing once it is full). A pipeline whose key has a file is created from its binaries without
        // compiling. A build that takes the store threshold or longer (PipelineBinaryStorePolicy, 20 ms by default) writes
        // its file at once, since a forced close saves nothing.
        // NVIDIA compresses the binaries with the dictionary of the executable's own shader cache file and adds that 2 MiB
        // dictionary to every graphics pipeline's binaries, so large binaries are kept once, by content, in the shared
        // folder. Once the driver starts a new cache file (a new dictionary), it refuses the old files: those pipelines
        // build and write their files again. A pipeline created from binaries or capturing them is created without a
        // pipeline cache, as Vulkan requires. SHARPEMU_VK_PIPELINE_BINARIES=0 or SHARPEMU_VK_PIPELINE_CACHE=0 turns this off.
        private const string PipelineBinaryExtensionName = "VK_KHR_pipeline_binary";
        private const int MaxPipelineBinaryRefusalReports = 8;
        private const int MaxPipelineBinariesPerFile = 16;

        // The data size written for a binary kept in the shared folder; its SHA-256 follows.
        private const int SharedPipelineBinaryMarker = -1;
        private KhrPipelineBinary? _pipelineBinaries;
        private string? _pipelineBinaryDirectory;
        private string _pipelineBinaryGlobalKey = "";
        private int _pipelineBinaryRefusals;
        private int _pipelineBinaryLoads;
        private int _pipelineBinaryStores;
        private readonly ConcurrentDictionary<string, byte[]> _sharedPipelineBinaries = new();
        private readonly PipelineBuildGate _pipelineBuilds = new();

        // A build at least this long stores its binaries (PipelineBinaryStorePolicy: SHARPEMU_PIPELINE_BINARY_MS, default
        // 20 ms), and a binary at least this large is shared (tests lower both).
        private double _pipelineBinarySlowMilliseconds = PipelineBinaryStorePolicy.Milliseconds;
        private int _sharedPipelineBinaryBytes = 1 << 20;

        private static bool PipelineBinariesRequested() =>
            Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE") != "0" &&
            Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_BINARIES") != "0";

        // Once the device exists: the files go beside the pipeline cache path, under the driver's global key, which a
        // driver change replaces.
        private void InitializePipelineBinaries(string directory)
        {
            if (!_vk.TryGetDeviceExtension(_instance, _device, out KhrPipelineBinary binaries))
            {
                Console.Error.WriteLine("[LOADER][WARN] Vulkan pipeline binaries unavailable: the extension did not load.");
                return;
            }

            var globalKey = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
            var result = binaries.GetPipelineKey(_device, null, &globalKey);
            if (result != Result.Success || globalKey.KeySize == 0)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan pipeline binaries unavailable: global key result={result}");
                return;
            }

            _pipelineBinaries = binaries;
            _pipelineBinaryDirectory = directory;
            _pipelineBinaryGlobalKey = Convert.ToHexString(new ReadOnlySpan<byte>(globalKey.Key, (int)globalKey.KeySize));
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan pipeline binaries ready: directory={directory} store_ms={_pipelineBinarySlowMilliseconds}");
        }

        // The first line of a pipeline's file: the format, the driver's global key and the pipeline key.
        private string PipelineBinarySignature(byte[] key) => $"SharpEmuPB2:{_pipelineBinaryGlobalKey}:{Convert.ToHexString(key)}\n";

        private static Result CreateGraphicsPipelineCall(Vk vk, Device device, PipelineCache cache, void* info, Pipeline* pipeline) =>
            vk.CreateGraphicsPipelines(device, cache, 1, (GraphicsPipelineCreateInfo*)info, null, pipeline);

        private static Result CreateComputePipelineCall(Vk vk, Device device, PipelineCache cache, void* info, Pipeline* pipeline) =>
            vk.CreateComputePipelines(device, cache, 1, (ComputePipelineCreateInfo*)info, null, pipeline);

        // Creates a pipeline from the binaries stored under its driver key, else builds it (capturing its data while
        // binaries are on) and stores the binaries of a slow build. next is the create info's pNext field. cache is used
        // only by a build that neither uses nor captures binaries (VUIDs pNext-09616 and pNext-09617). Safe on compile
        // workers: it reads only state that device setup wrote. One thread at a time creates a key (PipelineBuildGate), so
        // milliseconds includes any wait for another thread's build of it.
        private Result CreatePipelineWithBinaries(
            void* createInfo,
            void** next,
            PipelineCache cache,
            delegate*<Vk, Device, PipelineCache, void*, Pipeline*, Result> create,
            out Pipeline pipeline,
            out double milliseconds,
            PipelineCreateFlags2 extraFlags = 0)
        {
            var started = Stopwatch.GetTimestamp();
            var key = PipelineBinaryKey(createInfo);
            var build = key is null ? null : _pipelineBuilds.Begin(Convert.ToHexString(key));
            try
            {
                var result = LoadOrBuildPipeline(createInfo, next, cache, create, key, out pipeline, extraFlags);
                milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return result;
            }
            finally
            {
                if (build is not null)
                {
                    _pipelineBuilds.Finish(build);
                }
            }
        }

        private Result LoadOrBuildPipeline(
            void* createInfo,
            void** next,
            PipelineCache cache,
            delegate*<Vk, Device, PipelineCache, void*, Pipeline*, Result> create,
            byte[]? key,
            out Pipeline pipeline,
            PipelineCreateFlags2 extraFlags)
        {
            var started = Stopwatch.GetTimestamp();
            var original = *next;
            Pipeline created = default;
            Result result;
            if (key is not null && LoadPipelineBinaries(key) is { } loaded)
            {
                fixed (PipelineBinaryKHR* handles = loaded)
                {
                    var binaryInfo = new PipelineBinaryInfoKHR
                    {
                        SType = StructureType.PipelineBinaryInfoKhr,
                        PNext = original,
                        BinaryCount = (uint)loaded.Length,
                        PPipelineBinaries = handles,
                    };
                    *next = &binaryInfo;
                    result = create(_vk, _device, default, createInfo, &created);
                    *next = original;
                }

                foreach (var handle in loaded)
                {
                    _pipelineBinaries!.DestroyPipelineBinary(_device, handle, null);
                }

                if (result == Result.Success)
                {
                    ReportPipelineBinaryCount("loaded", Interlocked.Increment(ref _pipelineBinaryLoads));
                    pipeline = created;
                    return result;
                }

                ReportPipelineBinaryRefusal("create", key, result);
            }

            var capture = new PipelineCreateFlags2CreateInfoKHR
            {
                SType = StructureType.PipelineCreateFlags2CreateInfoKhr,
                PNext = original,
                Flags = PipelineCreateFlags2.Vk2CaptureDataBitKhr | extraFlags,
            };
            if (key is not null)
            {
                *next = &capture;
            }

            result = create(_vk, _device, key is not null ? default : cache, createInfo, &created);
            *next = original;
            if (key is not null && result == Result.Success)
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _pipelineBinarySlowMilliseconds)
                {
                    StorePipelineBinaries(created, key);
                }

                var release = new ReleaseCapturedPipelineDataInfoKHR
                {
                    SType = StructureType.ReleaseCapturedPipelineDataInfoKhr,
                    Pipeline = created,
                };
                _pipelineBinaries!.ReleaseCapturedPipelineData(_device, &release, null);
            }

            pipeline = created;
            return result;
        }

        // The driver's key for a create info and its pNext chain; null while binaries are off.
        private byte[]? PipelineBinaryKey(void* createInfo)
        {
            if (_pipelineBinaries is not { } binaries || _pipelineBinaryDirectory is null)
            {
                return null;
            }

            var info = new PipelineCreateInfoKHR { SType = StructureType.PipelineCreateInfoKhr, PNext = createInfo };
            var key = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
            return binaries.GetPipelineKey(_device, &info, &key) == Result.Success && key.KeySize != 0
                ? new ReadOnlySpan<byte>(key.Key, (int)key.KeySize).ToArray()
                : null;
        }

        private string PipelineBinaryPath(byte[] key) => Path.Combine(_pipelineBinaryDirectory!, Convert.ToHexString(key) + ".bin");

        // A pipeline's file is PipelineCacheSignature.Wrap of its signature and a payload: the binary count, then for each
        // binary its key size, key, data size and data, or SharedPipelineBinaryMarker and the SHA-256 of a binary in the
        // shared folder. A file of another format, driver or pipeline key is not read; a damaged one is refused.
        private PipelineBinaryKHR[]? LoadPipelineBinaries(byte[] key)
        {
            byte[] file;
            try
            {
                var path = PipelineBinaryPath(key);
                if (!File.Exists(path))
                {
                    return null;
                }

                file = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var signature = PipelineBinarySignature(key);
            if (!file.AsSpan().StartsWith(Encoding.ASCII.GetBytes(signature)))
            {
                return null;
            }

            if (!PipelineCacheSignature.TryUnwrap(signature, file, out var payload))
            {
                return Refused("hash");
            }

            var offset = 0;
            if (!TryReadInt32(payload, ref offset, out var count) || count is <= 0 or > MaxPipelineBinariesPerFile)
            {
                return Refused("parse");
            }

            var keyArray = new PipelineBinaryKeyKHR[count];
            var parts = new ReadOnlyMemory<byte>[count];
            for (var index = 0; index < count; index++)
            {
                if (!TryReadInt32(payload, ref offset, out var keyLength) || keyLength is <= 0 or > 32 ||
                    keyLength > payload.Length - offset)
                {
                    return Refused("parse");
                }

                var binaryKey = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr, KeySize = (uint)keyLength };
                payload.AsSpan(offset, keyLength).CopyTo(new Span<byte>(binaryKey.Key, keyLength));
                keyArray[index] = binaryKey;
                offset += keyLength;
                if (!TryReadInt32(payload, ref offset, out var dataLength))
                {
                    return Refused("parse");
                }

                if (dataLength == SharedPipelineBinaryMarker)
                {
                    if (SHA256.HashSizeInBytes > payload.Length - offset ||
                        LoadSharedPipelineBinary(payload.AsSpan(offset, SHA256.HashSizeInBytes)) is not { } shared)
                    {
                        return Refused("shared");
                    }

                    parts[index] = shared;
                    offset += SHA256.HashSizeInBytes;
                }
                else if (dataLength > 0 && dataLength <= payload.Length - offset)
                {
                    parts[index] = payload.AsMemory(offset, dataLength);
                    offset += dataLength;
                }
                else
                {
                    return Refused("parse");
                }
            }

            if (offset != payload.Length)
            {
                return Refused("parse");
            }

            var handles = new PipelineBinaryKHR[count];
            var pins = new MemoryHandle[count];
            Result result;
            try
            {
                fixed (PipelineBinaryKeyKHR* keyPointer = keyArray)
                fixed (PipelineBinaryKHR* handlePointer = handles)
                {
                    var dataArray = stackalloc PipelineBinaryDataKHR[count];
                    for (var index = 0; index < count; index++)
                    {
                        pins[index] = parts[index].Pin();
                        dataArray[index] = new PipelineBinaryDataKHR
                        {
                            DataSize = (nuint)parts[index].Length,
                            PData = pins[index].Pointer,
                        };
                    }

                    var keysAndData = new PipelineBinaryKeysAndDataKHR
                    {
                        BinaryCount = (uint)keyArray.Length,
                        PPipelineBinaryKeys = keyPointer,
                        PPipelineBinaryData = dataArray,
                    };
                    var createInfo = new PipelineBinaryCreateInfoKHR
                    {
                        SType = StructureType.PipelineBinaryCreateInfoKhr,
                        PKeysAndDataInfo = &keysAndData,
                    };
                    var handleInfo = new PipelineBinaryHandlesInfoKHR
                    {
                        SType = StructureType.PipelineBinaryHandlesInfoKhr,
                        PipelineBinaryCount = (uint)handles.Length,
                        PPipelineBinaries = handlePointer,
                    };
                    result = _pipelineBinaries!.CreatePipelineBinaries(_device, &createInfo, null, &handleInfo);
                }
            }
            finally
            {
                foreach (var pin in pins)
                {
                    pin.Dispose();
                }
            }

            if (result == Result.Success)
            {
                return handles;
            }

            foreach (var handle in handles)
            {
                if (handle.Handle != 0)
                {
                    _pipelineBinaries!.DestroyPipelineBinary(_device, handle, null);
                }
            }

            return Refused(result.ToString());

            PipelineBinaryKHR[]? Refused(string reason)
            {
                ReportPipelineBinaryRefusal(reason, key, result: null);
                return null;
            }
        }

        private static bool TryReadInt32(byte[] data, ref int offset, out int value)
        {
            value = 0;
            if (offset < 0 || sizeof(int) > data.Length - offset)
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += sizeof(int);
            return true;
        }

        // Writes the binaries of a pipeline created with captured data to its key's file.
        private void StorePipelineBinaries(Pipeline pipeline, byte[] key)
        {
            var binaries = _pipelineBinaries!;
            var createInfo = new PipelineBinaryCreateInfoKHR { SType = StructureType.PipelineBinaryCreateInfoKhr, Pipeline = pipeline };
            var handleInfo = new PipelineBinaryHandlesInfoKHR { SType = StructureType.PipelineBinaryHandlesInfoKhr };
            var result = binaries.CreatePipelineBinaries(_device, &createInfo, null, &handleInfo);
            if (result != Result.Success || handleInfo.PipelineBinaryCount is 0 or > MaxPipelineBinariesPerFile)
            {
                ReportPipelineBinaryRefusal("capture", key, result);
                return;
            }

            var handles = new PipelineBinaryKHR[handleInfo.PipelineBinaryCount];
            try
            {
                fixed (PipelineBinaryKHR* handlePointer = handles)
                {
                    handleInfo.PPipelineBinaries = handlePointer;
                    result = binaries.CreatePipelineBinaries(_device, &createInfo, null, &handleInfo);
                }

                if (result != Result.Success)
                {
                    ReportPipelineBinaryRefusal("capture", key, result);
                    return;
                }

                using var stream = new MemoryStream();
                stream.Write(BitConverter.GetBytes(handles.Length));
                foreach (var handle in handles)
                {
                    var dataInfo = new PipelineBinaryDataInfoKHR { SType = StructureType.PipelineBinaryDataInfoKhr, PipelineBinary = handle };
                    var binaryKey = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
                    nuint size = 0;
                    result = binaries.GetPipelineBinaryData(_device, &dataInfo, &binaryKey, &size, null);
                    if (result != Result.Success || size == 0)
                    {
                        ReportPipelineBinaryRefusal("data", key, result);
                        return;
                    }

                    var data = new byte[checked((int)size)];
                    fixed (byte* dataPointer = data)
                    {
                        result = binaries.GetPipelineBinaryData(_device, &dataInfo, &binaryKey, &size, dataPointer);
                    }

                    if (result != Result.Success)
                    {
                        ReportPipelineBinaryRefusal("data", key, result);
                        return;
                    }

                    stream.Write(BitConverter.GetBytes((int)binaryKey.KeySize));
                    stream.Write(new ReadOnlySpan<byte>(binaryKey.Key, (int)binaryKey.KeySize));
                    if (data.Length >= _sharedPipelineBinaryBytes)
                    {
                        stream.Write(BitConverter.GetBytes(SharedPipelineBinaryMarker));
                        stream.Write(StoreSharedPipelineBinary(data));
                    }
                    else
                    {
                        stream.Write(BitConverter.GetBytes(data.Length));
                        stream.Write(data);
                    }
                }

                WriteFileAtomically(PipelineBinaryPath(key), PipelineCacheSignature.Wrap(PipelineBinarySignature(key), stream.ToArray()));
                ReportPipelineBinaryCount("stored", Interlocked.Increment(ref _pipelineBinaryStores));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan pipeline binaries not saved: {exception.Message}");
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.Handle != 0)
                    {
                        binaries.DestroyPipelineBinary(_device, handle, null);
                    }
                }
            }
        }

        private string SharedPipelineBinaryPath(ReadOnlySpan<byte> hash) =>
            Path.Combine(_pipelineBinaryDirectory!, "shared", Convert.ToHexString(hash) + ".bin");

        // Writes a shared binary unless its file is there with the same contents; returns its SHA-256.
        private byte[] StoreSharedPipelineBinary(byte[] data)
        {
            var hash = SHA256.HashData(data);
            if (LoadSharedPipelineBinary(hash) is null)
            {
                WriteFileAtomically(SharedPipelineBinaryPath(hash), data);
                _sharedPipelineBinaries.TryAdd(Convert.ToHexString(hash), data);
            }

            return hash;
        }

        // A shared binary whose contents still match its hash; null when it is missing or changed.
        private byte[]? LoadSharedPipelineBinary(ReadOnlySpan<byte> hash)
        {
            var name = Convert.ToHexString(hash);
            if (_sharedPipelineBinaries.TryGetValue(name, out var known))
            {
                return known;
            }

            byte[] data;
            try
            {
                var path = SharedPipelineBinaryPath(hash);
                if (!File.Exists(path))
                {
                    return null;
                }

                data = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (!SHA256.HashData(data).AsSpan().SequenceEqual(hash))
            {
                return null;
            }

            _sharedPipelineBinaries.TryAdd(name, data);
            return data;
        }

        private static void WriteFileAtomically(string path, byte[] contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + $".{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }

        // Logs at counts 1, 2, 4, 8, ...
        private static void ReportPipelineBinaryCount(string what, int count)
        {
            if ((count & (count - 1)) == 0)
            {
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan pipeline binaries {what}: {count}");
            }
        }

        private void ReportPipelineBinaryRefusal(string stage, byte[] key, Result? result)
        {
            if (Interlocked.Increment(ref _pipelineBinaryRefusals) <= MaxPipelineBinaryRefusalReports)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline binaries refused: stage={stage} key={Convert.ToHexString(key)} result={result}");
            }
        }
    }
}
