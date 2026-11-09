// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool GpuLodStatistics =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOD_STATS"), "gpu", StringComparison.Ordinal);

    private static int _fragmentSubgroupArithmetic;

    internal static bool FragmentSubgroupArithmetic => Volatile.Read(ref _fragmentSubgroupArithmetic) != 0;

    private static void SetMipStatisticsCapabilities(bool fragmentStoresAndAtomics, SubgroupFeatureFlags operations, ShaderStageFlags stages)
    {
        ResourceMaterializer.RecordMipStatistics = GpuLodStatistics && fragmentStoresAndAtomics;
        var arithmetic = (operations & (SubgroupFeatureFlags.ArithmeticBit | SubgroupFeatureFlags.BallotBit | SubgroupFeatureFlags.BasicBit)) ==
                         (SubgroupFeatureFlags.ArithmeticBit | SubgroupFeatureFlags.BallotBit | SubgroupFeatureFlags.BasicBit) &&
                         (stages & ShaderStageFlags.FragmentBit) != 0;
        Volatile.Write(ref _fragmentSubgroupArithmetic, arithmetic ? 1 : 0);
    }

    private sealed partial class Presenter
    {
        internal LodStatisticsRecorder? _lodStatistics;

        internal LodStatisticsRecorder LodStatistics() =>
            _lodStatistics ??= new LodStatisticsRecorder(_deviceInfo, _scheduler);

        bool Gpu.Pipelines.IShaderPipelineHost.MipStatisticsSubgroupReduction => FragmentSubgroupArithmetic && GraphicsSubgroupOperationsEnabled;

        bool Gpu.GpuCommands.ICommandStreamHost.TryRecordLodStatistics(ulong destination, uint size)
        {
            if (!ResourceMaterializer.RecordMipStatistics)
            {
                if (!OnCommandThread)
                {
                    return false;
                }

                _commandThread!.PostRange(() => WriteBindingLodStatistics(destination, size), destination, size, CommandThread.RangeKind.Lod);
                return true;
            }

            if (size < MipStatistics.ReportBytes)
            {
                return false;
            }

            if (OnCommandThread)
            {
                _commandThread!.PostRange(() => RecordLodStatistics(destination, size), destination, size, CommandThread.RangeKind.Lod);
                return true;
            }

            RecordLodStatistics(destination, size);
            return true;
        }

        private void RecordLodStatistics(ulong destination, uint size)
        {
            Recording.EndRendering();
            EnsureBatchedGuestCommands();
            LodStatistics().Record(destination, size);
        }

        private void WriteBindingLodStatistics(ulong destination, uint size)
        {
            var report = new byte[size];
            MipStatistics.Shared.WriteReport(report);
            if (!((Gpu.GpuCommands.ICommandStreamHost)this).Memory.TryWrite(destination, report))
            {
                throw SubmissionScheduler.Fatal($"The LOD statistics report could not be written: address=0x{destination:X16} size={report.Length}.");
            }
        }

        private void PublishLodStatistics()
        {
            if (_lodStatistics is not { } recorder)
            {
                return;
            }

            while (recorder.TryTakeReport(out var destination, out var report))
            {
                if (!((Gpu.GpuCommands.ICommandStreamHost)this).Memory.TryWrite(destination, report))
                {
                    throw SubmissionScheduler.Fatal($"The LOD statistics report could not be written: address=0x{destination:X16} size={report.Length}.");
                }
            }
        }

        internal void WriteMipStatisticsFields(uint[] shaderData, BindingLayout layout, ShaderResourceInfo info, uint[][] images, uint[]? statistics)
        {
            for (var index = 0; index < layout.MipStatisticsCount; index++)
            {
                var image = index < info.Images.Count ? info.Images[index] : null;
                var words = index < images.Length ? images[index] : null;
                var current = statistics is not null && index < statistics.Length ? statistics[index] : ResourceSnapshot.NoMipStatistics;
                shaderData[layout.MipStatisticsDword + (uint)index] = image is { MipStatistics: true } && words is { Length: >= 8 }
                    ? MipStatistics.ImageField(words, current)
                    : MipStatistics.NoCounterField;
            }
        }

        internal DescriptorBufferInfo MipStatisticsDescriptor()
        {
            var counters = LodStatistics().Counters;
            return new DescriptorBufferInfo { Buffer = counters.Handle, Offset = 0, Range = counters.Size };
        }
    }

    private sealed class LodStatisticsRecorder
    {
        private const int Slots = 32;
        private const ulong SlotStride = 4096;

        private readonly SubmissionScheduler _scheduler;
        private readonly GpuBuffer _publish;
        private readonly ulong[] _slotTicks = new ulong[Slots];
        private readonly ConcurrentQueue<(ulong Destination, byte[] Report)> _ready = new();
        private int _nextSlot;
        private bool _initialized;

        public LodStatisticsRecorder(GpuDeviceInfo device, SubmissionScheduler scheduler)
        {
            _scheduler = scheduler;
            Counters = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, MipStatistics.CounterBufferBytes);
            _publish = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, Slots * SlotStride);
        }

        public GpuBuffer Counters { get; }

        public void Reset()
        {
            Counters.Fill(0, MipStatistics.CounterWords * sizeof(uint), uint.MaxValue);
            Counters.Fill(MipStatistics.CounterWords * sizeof(uint), MipStatistics.CounterWords * sizeof(uint), 0);
            _initialized = true;
        }

        public void EnsureInitialized()
        {
            if (!_initialized)
            {
                Reset();
            }
        }

        public void Record(ulong destination, uint size)
        {
            EnsureInitialized();
            var slot = _nextSlot;
            _nextSlot = (_nextSlot + 1) % Slots;
            if (_slotTicks[slot] != 0 && !_scheduler.IsTickComplete(_slotTicks[slot]))
            {
                _scheduler.Wait(_slotTicks[slot]);
            }

            var offset = (ulong)slot * SlotStride;
            _publish.CopyFrom(
                _scheduler.Current, Counters, 0, offset, MipStatistics.CounterBufferBytes,
                AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit, AccessFlags.None,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
            Reset();
            _slotTicks[slot] = _scheduler.CurrentTick;
            var reportSize = (int)Math.Min(size, (uint)MipStatistics.ReportBytes);
            _scheduler.QueueCompletionAction(() =>
            {
                _publish.Invalidate(offset, MipStatistics.CounterBufferBytes);
                var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                    _publish.Mapped.Slice((int)offset, (int)MipStatistics.CounterBufferBytes));
                var report = new byte[reportSize];
                MipStatistics.PackReport(words, report);
                _ready.Enqueue((destination, report));
            });
        }

        public bool TryTakeReport(out ulong destination, out byte[] report)
        {
            if (_ready.TryDequeue(out var entry))
            {
                (destination, report) = entry;
                return true;
            }

            destination = 0;
            report = [];
            return false;
        }
    }
}
