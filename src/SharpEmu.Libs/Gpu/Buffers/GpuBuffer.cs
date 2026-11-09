// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.VideoOut;
using System.Numerics;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Logging;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Buffers;

// One Vulkan buffer, mapped when host-visible. Small buffers share slab chunks (GpuMemorySlabs);
// larger ones get a dedicated allocation.
public unsafe class GpuBuffer : IDisposable
{
    public const BufferUsageFlags ReadFlags =
        BufferUsageFlags.TransferSrcBit | BufferUsageFlags.UniformBufferBit | BufferUsageFlags.IndexBufferBit |
        BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndirectBufferBit;

    public const BufferUsageFlags AllFlags = ReadFlags | BufferUsageFlags.TransferDstBit | BufferUsageFlags.StorageBufferBit;

    public const BufferUsageFlags AddressedFlags = AllFlags | BufferUsageFlags.ShaderDeviceAddressBit;

    private const AccessFlags MemoryAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
    private const AccessFlags HostAccess = AccessFlags.HostReadBit | AccessFlags.HostWriteBit;

    private static readonly SharpEmuLogger Log = SharpEmuLog.For("GPU");
    private static readonly int[] _loggedMemoryTypes = new int[(int)GpuBufferUsage.MappedDeviceLocal + 1];
    private static readonly int[] _loggedFallbackTypes = new int[(int)GpuBufferUsage.MappedDeviceLocal + 1];

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly ulong _allocationSize;
    private readonly byte* _mapped;
    private readonly ulong _deviceAddress;
    private VkBuffer _handle;
    private DeviceMemory _memory;
    private readonly ulong _memoryOffset;
    private readonly GpuMemorySlabs.Block? _slab;

    // allowSlab: the caller records on the scheduler's current command buffer, which clears a recycled slab block.
    public GpuBuffer(GpuDeviceInfo device, SubmissionScheduler scheduler, GpuBufferUsage usage, ulong cpuAddress, BufferUsageFlags flags, ulong size,
        bool allowSlab = false)
    {
        if (size == 0)
        {
            throw SubmissionScheduler.Fatal("The buffer size is invalid.");
        }

        _device = device;
        _scheduler = scheduler;
        Usage = usage;
        CpuAddress = cpuAddress;
        Size = size;

        var vk = device.Vk;
        var sharedFamilies = device.SharedQueueFamilies;
        fixed (uint* families = sharedFamilies)
        {
            // Buffers carry no layout, so concurrent sharing lets the readback queue copy
            // them without queue-family ownership transfers at no cost to other queues.
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = flags,
                SharingMode = sharedFamilies is { Length: > 1 } ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = sharedFamilies is { Length: > 1 } ? (uint)sharedFamilies.Length : 0,
                PQueueFamilyIndices = sharedFamilies is { Length: > 1 } ? families : null,
            };
            RequireSuccess(vk.CreateBuffer(device.Device, &bufferInfo, null, out _handle), "vkCreateBuffer");
        }

        vk.GetBufferMemoryRequirements(device.Device, _handle, out var requirements);
        var withAddress = (flags & BufferUsageFlags.ShaderDeviceAddressBit) != 0;
        var flagsInfo = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit,
        };
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = withAddress ? &flagsInfo : null,
            AllocationSize = requirements.Size,
        };

        // The best-scored type first; a full heap falls through to the next candidate.
        var result = Result.ErrorOutOfDeviceMemory;
        var candidates = RankAllocationCandidates(device, requirements.MemoryTypeBits, usage);
        var chosen = -1;
        var slabFailed = false;
        var firstFailure = Result.Success;
        if (allowSlab && GpuMemorySlabs.Fits(requirements))
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (device.Slabs.TryAllocate(candidates[index], withAddress, requirements, out var block))
                {
                    _slab = block;
                    _memory = block.Memory;
                    _memoryOffset = block.Offset;
                    _mapped = block.Mapped;
                    allocateInfo.MemoryTypeIndex = candidates[index];
                    result = Result.Success;
                    chosen = index;
                    break;
                }

                slabFailed = true;
            }
        }

        if (_slab is null)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                allocateInfo.MemoryTypeIndex = candidates[index];
                result = device.AllocateMemory(allocateInfo, out _memory);
                if (result == Result.Success)
                {
                    chosen = index;
                    break;
                }

                if (firstFailure == Result.Success)
                {
                    firstFailure = result;
                }
            }
        }

        RequireSuccess(result, $"vkAllocateMemory({usage}, 0x{size:X} bytes)");
        RequireSuccess(vk.BindBufferMemory(device.Device, _handle, _memory, _memoryOffset), "vkBindBufferMemory");
        _allocationSize = _slab?.Size ?? requirements.Size;
        var properties = device.GetMemoryTypeFlags(allocateInfo.MemoryTypeIndex);
        IsCoherent = (properties & MemoryPropertyFlags.HostCoherentBit) != 0;
        NotePlacement(usage, allocateInfo.MemoryTypeIndex, properties);
        if (chosen > 0)
        {
            NoteFallback(device, usage, size, requirements.MemoryTypeBits, candidates[0], allocateInfo.MemoryTypeIndex, _slab is not null,
                slabFailed && firstFailure == Result.Success ? "the slab chunk allocation failed" : firstFailure.ToString());
        }
        if (_slab is null && (properties & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* pointer;
            RequireSuccess(vk.MapMemory(device.Device, _memory, 0, Vk.WholeSize, 0, &pointer), "vkMapMemory");
            _mapped = (byte*)pointer;
        }

        if (withAddress)
        {
            var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = _handle };
            _deviceAddress = vk.GetBufferDeviceAddress(device.Device, &addressInfo);
            if (_deviceAddress == 0)
            {
                throw SubmissionScheduler.Fatal("The buffer device address is unavailable.");
            }
        }

        // A dedicated allocation arrives zeroed; keep that for a block another buffer used.
        if (_slab is { Recycled: true } && (Size & ~3UL) != 0)
        {
            Fill(0, Size & ~3UL, 0);
        }
    }

    public VkBuffer Handle => _handle;

    public ulong Size { get; }

    public Span<byte> Mapped => _mapped == null ? Span<byte>.Empty : new Span<byte>(_mapped, checked((int)Size));

    public Span<byte> MappedRange(ulong offset, int length)
    {
        if (_mapped == null || length < 0 || offset > Size || (ulong)length > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The mapped buffer range is invalid.");
        }

        return new Span<byte>(_mapped + offset, length);
    }

    public bool IsCoherent { get; }

    public bool IsMapped => _mapped != null;

    public GpuBufferUsage Usage { get; }

    public int Readbacks { get; private set; }

    public void NoteReadback() => Readbacks++;

    public void ForgetReadbacks() => Readbacks = 0;

    public ulong CpuAddress { get; }

    public ulong DeviceAddress => _deviceAddress != 0 ? _deviceAddress : throw SubmissionScheduler.Fatal("The buffer has no device address.");

    public int StreamScore { get; private set; }

    protected GpuDeviceInfo Device => _device;

    protected SubmissionScheduler Scheduler => _scheduler;

    public ulong Offset(ulong address) => address - CpuAddress;

    public bool IsInBounds(ulong address, ulong size) =>
        address >= CpuAddress && size <= Size && address - CpuAddress <= Size - size;

    public void AddStreamScore(int score) => StreamScore += score;

    // The highest scheduler tick whose command buffer may write this buffer on the GPU.
    // A readback of it only has to wait for that tick, not for all queued work.
    public ulong LastGpuWriteTick { get; private set; }

    private readonly WriteTickMap _writeTicks = new();
    private int _writeTickPruneCount = 64;

    // Called by every path that records a GPU write into this buffer.
    public void NoteGpuWrite() => NoteGpuWrite(0, Size);

    public long GpuWrites { get; private set; }

    public void NoteGpuWrite(ulong offset, ulong size)
    {
        GpuWrites++;
        var tick = _scheduler.CurrentTick;
        if (tick > LastGpuWriteTick)
        {
            LastGpuWriteTick = tick;
        }

        if (_mapped != null)
        {
            _scheduler.NoteMappedMemoryWrite();
        }

        if (size == 0 || offset >= Size)
        {
            return;
        }

        _writeTicks.Assign(offset, Math.Min(size, Size - offset), tick);
        if (_writeTicks.Count >= _writeTickPruneCount)
        {
            if (Volatile.Read(ref _foreignReads) == 0)
            {
                _writeTicks.Prune(_scheduler.Timeline.CompletedTick + 1);
            }

            _writeTickPruneCount = Math.Max(64, _writeTicks.Count * 2);
        }
    }

    public ulong LastGpuWriteTickIn(ulong offset, ulong size) => _writeTicks.MaxTick(offset, size);

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        if (_mapped == null || offset > Size || (ulong)source.Length > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The mapped buffer write range is invalid.");
        }

        source.CopyTo(Mapped[(int)offset..]);
        Flush(offset, (ulong)source.Length);
    }

    public void Flush(ulong offset, ulong size)
    {
        if (_mapped == null || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The buffer flush range is invalid.");
        }

        if (!IsCoherent && size != 0)
        {
            var range = GetMappedRange(offset, size);
            RequireSuccess(_device.Vk.FlushMappedMemoryRanges(_device.Device, 1, &range), "vkFlushMappedMemoryRanges");
        }
    }

    public void Invalidate(ulong offset, ulong size)
    {
        if (_mapped == null || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The buffer invalidation range is invalid.");
        }

        if (!IsCoherent && size != 0)
        {
            var range = GetMappedRange(offset, size);
            RequireSuccess(_device.Vk.InvalidateMappedMemoryRanges(_device.Device, 1, &range), "vkInvalidateMappedMemoryRanges");
        }
    }

    public ReadOnlySpan<byte> ReadMapped(ulong offset, ulong size)
    {
        Invalidate(offset, size);
        return new ReadOnlySpan<byte>(_mapped + offset, checked((int)size));
    }

    public void CopyFrom(
        RecordingBuffer command,
        GpuBuffer source,
        ulong sourceOffset,
        ulong destinationOffset,
        ulong size,
        AccessFlags sourceBefore = AccessFlags.MemoryWriteBit,
        AccessFlags destinationBefore = MemoryAccess,
        AccessFlags sourceAfter = MemoryAccess,
        AccessFlags destinationAfter = MemoryAccess)
    {
        if (size == 0 || sourceOffset > source.Size || size > source.Size - sourceOffset || destinationOffset > Size || size > Size - destinationOffset)
        {
            throw SubmissionScheduler.Fatal("The buffer copy range is invalid.");
        }

        if (source.Handle.Handle == Handle.Handle && sourceOffset < destinationOffset + size && destinationOffset < sourceOffset + size)
        {
            throw SubmissionScheduler.Fatal("Cannot copy overlapping ranges of the same buffer.");
        }

        command.EndRendering();
        NoteGpuWrite(destinationOffset, size);
        if (IsMapped)
        {
            command.NoteMappedMemoryWrite();
        }

        var vk = _device.Vk;
        var native = new CommandBuffer(command.TransferHandle);
        var before = stackalloc BufferMemoryBarrier2[2];
        before[0] = source.CreateBarrier(sourceOffset, size, sourceBefore, AccessFlags.TransferReadBit);
        before[1] = CreateBarrier(destinationOffset, size, destinationBefore, AccessFlags.TransferWriteBit);
        VulkanSynchronization.PipelineBarrier(vk,
            native, GetAccessStage(sourceBefore | destinationBefore), PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
            0, null, 2, before, 0, null, BarrierSource.Copy);
        var copy = new BufferCopy(sourceOffset, destinationOffset, size);
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
        {
            vk.CmdCopyBuffer(native, source.Handle, Handle, 1, &copy);
        }
        var after = stackalloc BufferMemoryBarrier2[2];
        after[0] = source.CreateBarrier(sourceOffset, size, AccessFlags.TransferReadBit, sourceAfter);
        after[1] = CreateBarrier(destinationOffset, size, AccessFlags.TransferWriteBit, destinationAfter);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.TransferBit, GetAccessStage(sourceAfter | destinationAfter), DependencyFlags.ByRegionBit,
            0, null, 2, after, 0, null, BarrierSource.Copy);
    }

    public sealed class CopyRun
    {
        private static readonly ThreadCounter _runs = new();
        private static readonly ThreadCounter _copies = new();

        private readonly record struct PendingCopy(GpuBuffer Source, ulong SourceOffset, GpuBuffer Destination, ulong DestinationOffset, ulong Size);

        private readonly List<PendingCopy> _pending = [];
        private BufferMemoryBarrier2[] _barriers = new BufferMemoryBarrier2[16];
        private BufferCopy[] _regions = new BufferCopy[8];
        private RecordingBuffer? _command;
        private AccessFlags _sourceBefore;
        private AccessFlags _destinationBefore;
        private AccessFlags _sourceAfter;
        private AccessFlags _destinationAfter;

        public int Count => _pending.Count;

        public int Flushes { get; private set; }

        public void Begin(
            RecordingBuffer command,
            AccessFlags sourceBefore = AccessFlags.MemoryWriteBit,
            AccessFlags destinationBefore = MemoryAccess,
            AccessFlags sourceAfter = MemoryAccess,
            AccessFlags destinationAfter = MemoryAccess)
        {
            if (_pending.Count != 0)
            {
                throw SubmissionScheduler.Fatal("A buffer copy run began before the previous run was recorded.");
            }

            _command = command;
            _sourceBefore = sourceBefore;
            _destinationBefore = destinationBefore;
            _sourceAfter = sourceAfter;
            _destinationAfter = destinationAfter;
        }

        public void Add(GpuBuffer source, ulong sourceOffset, GpuBuffer destination, ulong destinationOffset, ulong size)
        {
            if (_command == null)
            {
                throw SubmissionScheduler.Fatal("The buffer copy run has no command buffer.");
            }

            if (size == 0 || sourceOffset > source.Size || size > source.Size - sourceOffset ||
                destinationOffset > destination.Size || size > destination.Size - destinationOffset)
            {
                throw SubmissionScheduler.Fatal("The buffer copy range is invalid.");
            }

            if (Overlaps(source, sourceOffset, size, destination, destinationOffset, size))
            {
                throw SubmissionScheduler.Fatal("Cannot copy overlapping ranges of the same buffer.");
            }

            foreach (var pending in _pending)
            {
                if (Overlaps(pending.Destination, pending.DestinationOffset, pending.Size, source, sourceOffset, size) ||
                    Overlaps(pending.Destination, pending.DestinationOffset, pending.Size, destination, destinationOffset, size) ||
                    Overlaps(pending.Source, pending.SourceOffset, pending.Size, destination, destinationOffset, size))
                {
                    Flush();
                    break;
                }
            }

            _pending.Add(new PendingCopy(source, sourceOffset, destination, destinationOffset, size));
        }

        public void Flush()
        {
            var count = _pending.Count;
            if (count == 0)
            {
                return;
            }

            var command = _command ?? throw SubmissionScheduler.Fatal("The buffer copy run has no command buffer.");
            command.EndRendering();
            if (_barriers.Length < count * 2)
            {
                _barriers = new BufferMemoryBarrier2[Math.Max(count * 2, _barriers.Length * 2)];
            }

            if (_regions.Length < count)
            {
                _regions = new BufferCopy[Math.Max(count, _regions.Length * 2)];
            }

            var vk = _pending[0].Destination._device.Vk;
            var native = new CommandBuffer(command.TransferHandle);
            fixed (BufferMemoryBarrier2* barriers = _barriers)
            fixed (BufferCopy* regions = _regions)
            {
                for (var index = 0; index < count; index++)
                {
                    var copy = _pending[index];
                    barriers[index * 2] = copy.Source.CreateBarrier(copy.SourceOffset, copy.Size, _sourceBefore, AccessFlags.TransferReadBit);
                    barriers[index * 2 + 1] = copy.Destination.CreateBarrier(copy.DestinationOffset, copy.Size, _destinationBefore, AccessFlags.TransferWriteBit);
                }

                VulkanSynchronization.PipelineBarrier(vk,
                    native, GetAccessStage(_sourceBefore | _destinationBefore), PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
                    0, null, (uint)count * 2, barriers, 0, null, BarrierSource.CopyRun);
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    for (var first = 0; first < count;)
                    {
                        var pair = _pending[first];
                        var end = first;
                        while (end < count && ReferenceEquals(_pending[end].Source, pair.Source) && ReferenceEquals(_pending[end].Destination, pair.Destination))
                        {
                            var copy = _pending[end];
                            copy.Destination.NoteGpuWrite(copy.DestinationOffset, copy.Size);
                            if (copy.Destination.IsMapped)
                            {
                                command.NoteMappedMemoryWrite();
                            }

                            regions[end - first] = new BufferCopy(copy.SourceOffset, copy.DestinationOffset, copy.Size);
                            end++;
                        }

                        vk.CmdCopyBuffer(native, pair.Source.Handle, pair.Destination.Handle, (uint)(end - first), regions);
                        first = end;
                    }
                }

                for (var index = 0; index < count; index++)
                {
                    var copy = _pending[index];
                    barriers[index * 2] = copy.Source.CreateBarrier(copy.SourceOffset, copy.Size, AccessFlags.TransferReadBit, _sourceAfter);
                    barriers[index * 2 + 1] = copy.Destination.CreateBarrier(copy.DestinationOffset, copy.Size, AccessFlags.TransferWriteBit, _destinationAfter);
                }

                VulkanSynchronization.PipelineBarrier(vk,
                    native, PipelineStageFlags.TransferBit, GetAccessStage(_sourceAfter | _destinationAfter), DependencyFlags.ByRegionBit,
                    0, null, (uint)count * 2, barriers, 0, null, BarrierSource.CopyRun);
            }

            _runs.Increment();
            _copies.Add(count);
            Flushes++;
            _pending.Clear();
        }

        public void End()
        {
            Flush();
            _command = null;
        }

        public static string TakeReport() => FormattableString.Invariant($"copy_runs={_runs.Take()}/{_copies.Take()}");

        private static bool Overlaps(GpuBuffer first, ulong firstOffset, ulong firstSize, GpuBuffer second, ulong secondOffset, ulong secondSize) =>
            first.Handle.Handle == second.Handle.Handle && firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;
    }

    public void Fill(ulong offset, ulong size, uint value)
    {
        if (((offset | size) & 3) != 0)
        {
            throw SubmissionScheduler.Fatal("The buffer fill range must be aligned to four bytes.");
        }

        var command = _scheduler.Current;
        command.EndRendering();
        NoteGpuWrite(offset, size);
        var vk = _device.Vk;
        var native = new CommandBuffer(command.TransferHandle);
        var before = CreateBarrier(offset, size, MemoryAccess, AccessFlags.TransferWriteBit);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
            0, null, 1, &before, 0, null, BarrierSource.Fill);
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
        {
            vk.CmdFillBuffer(native, Handle, offset, size, value);
        }
        var after = CreateBarrier(offset, size, AccessFlags.TransferWriteBit, MemoryAccess);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
            0, null, 1, &after, 0, null, BarrierSource.Fill);
    }

    private int _foreignReads;
    private int _disposeDeferred;

    public void RetainForeignRead() => Interlocked.Increment(ref _foreignReads);

    public void ReleaseForeignRead()
    {
        if (Interlocked.Decrement(ref _foreignReads) == 0 && Interlocked.Exchange(ref _disposeDeferred, 0) != 0)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_handle.Handle == 0)
        {
            return;
        }

        if (Volatile.Read(ref _foreignReads) != 0)
        {
            Volatile.Write(ref _disposeDeferred, 1);
            if (Volatile.Read(ref _foreignReads) != 0 || Interlocked.Exchange(ref _disposeDeferred, 0) == 0)
            {
                return;
            }
        }

        _device.Vk.DestroyBuffer(_device.Device, _handle, null);
        if (_slab is { } slab)
        {
            _device.Slabs.Release(slab);
        }
        else
        {
            _device.FreeMemory(_memory);
        }

        _handle = default;
        _memory = default;
    }

    // Host access in a mask adds the host stage to the barrier.
    private static PipelineStageFlags GetAccessStage(AccessFlags access) =>
        (access & HostAccess) != 0
            ? PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit
            : PipelineStageFlags.AllCommandsBit;

    private static void NotePlacement(GpuBufferUsage usage, uint memoryType, MemoryPropertyFlags properties)
    {
        var bit = 1 << (int)memoryType;
        ref var logged = ref _loggedMemoryTypes[(int)usage];
        if ((Volatile.Read(ref logged) & bit) != 0 || (Interlocked.Or(ref logged, bit) & bit) != 0)
        {
            return;
        }

        Log.Info(FormattableString.Invariant($"{usage} buffers use memory type {memoryType}: {properties}"));
    }

    private static void NoteFallback(GpuDeviceInfo device, GpuBufferUsage usage, ulong size, uint typeBits, uint preferredType, uint memoryType,
        bool slab, string reason)
    {
        var preferred = device.GetMemoryTypeFlags(preferredType);
        var properties = device.GetMemoryTypeFlags(memoryType);
        var bit = 1 << (int)memoryType;
        ref var logged = ref _loggedFallbackTypes[(int)usage];
        if ((preferred & ~properties) == 0 || (Volatile.Read(ref logged) & bit) != 0 || (Interlocked.Or(ref logged, bit) & bit) != 0)
        {
            return;
        }

        Log.Warn(FormattableString.Invariant($"A {usage} buffer of 0x{size:X} bytes ({(slab ? "slab block" : "dedicated")}, allowed memory types 0x{typeBits:X}) fell back from memory type {preferredType} ({preferred}) to {memoryType} ({properties}) after {reason}"));
    }

    private static uint[] RankAllocationCandidates(GpuDeviceInfo device, uint typeBits, GpuBufferUsage usage)
    {
        var ranked = RankMemoryTypes(device, typeBits, usage);
        return usage == GpuBufferUsage.MappedDeviceLocal
            ? ranked.Concat(RankMemoryTypes(device, typeBits, GpuBufferUsage.DeviceLocal)).Distinct().ToArray()
            : ranked.ToArray();
    }

    internal static IEnumerable<uint> RankMemoryTypes(GpuDeviceInfo device, uint typeBits, GpuBufferUsage usage)
    {
        var (required, preferred, avoided) = usage switch
        {
            GpuBufferUsage.DeviceLocal => (MemoryPropertyFlags.None, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None),
            GpuBufferUsage.MappedDeviceLocal => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.HostCachedBit),
            GpuBufferUsage.Upload => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit),
            GpuBufferUsage.Download => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit, MemoryPropertyFlags.DeviceLocalBit),
            GpuBufferUsage.HostCachedStream => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit, MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit),
            _ => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None),
        };
        var candidates = new List<(int Score, uint Index)>();
        for (uint index = 0; index < device.MemoryTypeCount; index++)
        {
            var flags = device.GetMemoryTypeFlags(index);
            if ((typeBits & (1u << (int)index)) != 0 && (flags & required) == required)
            {
                var score = BitOperations.PopCount((uint)(flags & preferred)) - BitOperations.PopCount((uint)(flags & avoided));
                candidates.Add((score, index));
            }
        }

        if (candidates.Count == 0 && usage == GpuBufferUsage.HostCachedStream)
        {
            return RankMemoryTypes(device, typeBits, GpuBufferUsage.Upload);
        }

        return candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Index).Select(candidate => candidate.Index);
    }

    private MappedMemoryRange GetMappedRange(ulong offset, ulong size)
    {
        var atom = _device.NonCoherentAtomSize;
        var begin = offset / atom * atom;
        var end = Math.Min((offset + size + atom - 1) / atom * atom, _allocationSize);
        return new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = _memory,
            Offset = _memoryOffset + begin,
            Size = end - begin,
        };
    }

    private BufferMemoryBarrier2 CreateBarrier(ulong offset, ulong size, AccessFlags source, AccessFlags destination)
    {
        if (Handle.Handle == 0 || size == 0 || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal(
                $"The DMA barrier range is invalid: handle=0x{Handle.Handle:X} offset=0x{offset:X16} size=0x{size:X16} capacity=0x{Size:X16}");
        }

        return new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = VulkanSynchronization.Access(source),
            DstAccessMask = VulkanSynchronization.Access(destination),
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = Handle,
            Offset = offset,
            Size = size,
        };
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}
