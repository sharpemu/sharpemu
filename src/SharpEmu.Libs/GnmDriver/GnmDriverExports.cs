// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.GnmDriver;

public static class GnmDriverExports
{
    private static int Ok(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // ---- Initialization and core ----

    [SysAbiExport(ExportName = "_sceGnmMangle0", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int _sceGnmMangle0(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "_sceGnmMangle1", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int _sceGnmMangle1(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "_sceGnmMangle2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int _sceGnmMangle2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "_sceGnmMangle3", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int _sceGnmMangle3(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "_sceGnmMangle4", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int _sceGnmMangle4(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmAddEqEvent", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmAddEqEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmAreSubmitsAllowed", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmAreSubmitsAllowed(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmBeginWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmBeginWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmCompositorServerInit", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmCompositorServerInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmComputeWaitOnAddress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmComputeWaitOnAddress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmComputeWaitSemaphore", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmComputeWaitSemaphore(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmCreateWorkloadStream", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmCreateWorkloadStream(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebugHardwareStatus", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebugHardwareStatus(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebugModuleReset", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebugModuleReset(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebugReset", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebugReset(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerGetAddressWatch", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerGetAddressWatch(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerHaltWavefront", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerHaltWavefront(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerReadGds", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerReadGds(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerReadSqIndirectRegister", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerReadSqIndirectRegister(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerResumeWavefront", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerResumeWavefront(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerResumeWavefrontCreation", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerResumeWavefrontCreation(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerSetAddressWatch", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerSetAddressWatch(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerWriteGds", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerWriteGds(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDebuggerWriteSqIndirectRegister", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDebuggerWriteSqIndirectRegister(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDeleteEqEvent", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDeleteEqEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDestroyWorkloadStream", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDestroyWorkloadStream(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDingDong", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDingDong(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDingDongForWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDingDongForWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDisableMipStatsReport", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDisableMipStatsReport(CpuContext ctx) => Ok(ctx);

    // ---- Shader binding ----

    [SysAbiExport(ExportName = "sceGnmDispatchDirect", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDispatchDirect(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDispatchIndirect", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDispatchIndirect(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDispatchIndirectOnMec", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDispatchIndirectOnMec(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDispatchInitDefaultHardwareState", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDispatchInitDefaultHardwareState(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDisplayRenderTarget", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDisplayRenderTarget(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndex", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndex(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexAuto", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexAuto(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexIndirect", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexIndirect(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexIndirectCountMulti", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexIndirectCountMulti(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexIndirectMulti", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexIndirectMulti(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexMultiInstanced", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexMultiInstanced(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndexOffset", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndexOffset(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndirect", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndirect(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndirectCountMulti", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndirectCountMulti(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawIndirectMulti", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawIndirectMulti(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitDefaultHardwareState", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitDefaultHardwareState(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitDefaultHardwareState175", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitDefaultHardwareState175(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitDefaultHardwareState200", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitDefaultHardwareState200(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitDefaultHardwareState350", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitDefaultHardwareState350(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitToDefaultContextState", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitToDefaultContextState(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitToDefaultContextState400", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitToDefaultContextState400(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitToDefaultContextStateInternalCommand", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitToDefaultContextStateInternalCommand(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawInitToDefaultContextStateInternalSize", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawInitToDefaultContextStateInternalSize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDrawOpaqueAuto", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDrawOpaqueAuto(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverCaptureInProgress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverCaptureInProgress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterface", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterface(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForGpuDebugger", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForGpuDebugger(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForGpuException", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForGpuException(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForHDRScopes", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForHDRScopes(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForReplay", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForReplay(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForResourceRegistration", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForResourceRegistration(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalRetrieveGnmInterfaceForValidation", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalRetrieveGnmInterfaceForValidation(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverInternalVirtualQuery", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverInternalVirtualQuery(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverTraceInProgress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverTraceInProgress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmDriverTriggerCapture", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmDriverTriggerCapture(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmEndWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmEndWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmFindResources", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmFindResources(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmFindResourcesPublic", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmFindResourcesPublic(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmFlushGarlic", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmFlushGarlic(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetCoredumpAddress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetCoredumpAddress(CpuContext ctx) => Ok(ctx);

    // ---- Draw state ----

    [SysAbiExport(ExportName = "sceGnmGetCoredumpMode", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetCoredumpMode(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetCoredumpProtectionFaultTimestamp", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetCoredumpProtectionFaultTimestamp(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetDbgGcHandle", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetDbgGcHandle(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetDebugTimestamp", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetDebugTimestamp(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetEqEventType", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetEqEventType(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetEqTimeStamp", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetEqTimeStamp(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetGpuBlockStatus", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetGpuBlockStatus(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetGpuCoreClockFrequency", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetGpuCoreClockFrequency(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetGpuInfoStatus", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetGpuInfoStatus(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetLastWaitedAddress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetLastWaitedAddress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetNumTcaUnits", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetNumTcaUnits(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetOffChipTessellationBufferSize", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetOffChipTessellationBufferSize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetOwnerName", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetOwnerName(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetPhysicalCounterFromVirtualized", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetPhysicalCounterFromVirtualized(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetPhysicalCounterFromVirtualizedInternal", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetPhysicalCounterFromVirtualizedInternal(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetProtectionFaultTimeStamp", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetProtectionFaultTimeStamp(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceBaseAddressAndSizeInBytes", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceBaseAddressAndSizeInBytes(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceName", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceName(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceRegistrationBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceRegistrationBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceShaderGuid", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceShaderGuid(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceType", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceType(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetResourceUserData", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetResourceUserData(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetShaderProgramBaseAddress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetShaderProgramBaseAddress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetShaderStatus", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetShaderStatus(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGetTheTessellationFactorRingBufferBaseAddress", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGetTheTessellationFactorRingBufferBaseAddress(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGpuPaDebugEnter", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGpuPaDebugEnter(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmGpuPaDebugLeave", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmGpuPaDebugLeave(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInitialize", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertDingDongMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertDingDongMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertPopMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertPopMarker(CpuContext ctx) => Ok(ctx);

    // ---- Command buffer submission ----

    [SysAbiExport(ExportName = "sceGnmInsertPushColorMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertPushColorMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertPushMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertPushMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertSetColorMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertSetColorMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertSetMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertSetMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertThreadTraceMarker", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertThreadTraceMarker(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmInsertWaitFlipDone", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmInsertWaitFlipDone(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmIsCoredumpValid", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmIsCoredumpValid(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmIsUserPaEnabled", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmIsUserPaEnabled(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmKickCommandBuffer", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmKickCommandBuffer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmKickCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmKickCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmLogicalCuIndexToPhysicalCuIndex", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmLogicalCuIndexToPhysicalCuIndex(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmLogicalCuMaskToPhysicalCuMask", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmLogicalCuMaskToPhysicalCuMask(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmLogicalTcaUnitToPhysical", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmLogicalTcaUnitToPhysical(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmMapComputeQueue", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmMapComputeQueue(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmMapComputeQueueWithPriority", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmMapComputeQueueWithPriority(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmPaDisableFlipCallbacks", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmPaDisableFlipCallbacks(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmPaEnableFlipCallbacks", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmPaEnableFlipCallbacks(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmPaHeartbeat", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmPaHeartbeat(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmQueryResourceRegistrationUserMemoryRequirements", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmQueryResourceRegistrationUserMemoryRequirements(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRaiseUserExceptionEvent", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRaiseUserExceptionEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterGdsResource", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterGdsResource(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterGnmLiveCallbackConfig", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterGnmLiveCallbackConfig(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterOwner", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterOwner(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterOwnerForSystem", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterOwnerForSystem(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterRenderTargetForDisplay", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterRenderTargetForDisplay(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRegisterResource", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRegisterResource(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRequestFlipAndSubmitDone", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRequestFlipAndSubmitDone(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRequestFlipAndSubmitDoneForWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRequestFlipAndSubmitDoneForWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmRequestMipStatsReportAndReset", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmRequestMipStatsReportAndReset(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmResetVgtControl", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmResetVgtControl(CpuContext ctx) => Ok(ctx);

    // ---- Resource management ----

    [SysAbiExport(ExportName = "sceGnmSdmaClose", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaClose(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaConstFill", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaConstFill(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaCopyLinear", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaCopyLinear(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaCopyTiled", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaCopyTiled(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaCopyWindow", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaCopyWindow(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaFlush", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaFlush(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaGetMinCmdSize", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaGetMinCmdSize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSdmaOpen", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSdmaOpen(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetCsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetCsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetCsShaderWithModifier", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetCsShaderWithModifier(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetDisplayMode", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetDisplayMode(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetEmbeddedPsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetEmbeddedPsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetEmbeddedVsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetEmbeddedVsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetEsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetEsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetGsRingSizes", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetGsRingSizes(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetGsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetGsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetHsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetHsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetLsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetLsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetPsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetPsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetPsShader350", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetPsShader350(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetResourceRegistrationUserMemory", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetResourceRegistrationUserMemory(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetResourceUserData", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetResourceUserData(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetSpiEnableSqCounters", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetSpiEnableSqCounters(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetSpiEnableSqCountersForUnitInstance", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetSpiEnableSqCountersForUnitInstance(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetVgtControl", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetVgtControl(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetVsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetVsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetWaveLimitMultiplier", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetWaveLimitMultiplier(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetWaveLimitMultipliers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetWaveLimitMultipliers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSetupMipStatsReport", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSetupMipStatsReport(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmEndSpm", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmEndSpm(CpuContext ctx) => Ok(ctx);

    // ---- Validation ----

    [SysAbiExport(ExportName = "sceGnmSpmInit", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmInit2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmInit2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetDelay", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetDelay(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetMuxRam", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetMuxRam(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetMuxRam2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetMuxRam2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetSelectCounter", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetSelectCounter(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetSpmSelects", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetSpmSelects(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmSetSpmSelects2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmSetSpmSelects2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSpmStartSpm", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSpmStartSpm(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttFini", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttFini(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttFinishTrace", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttFinishTrace(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetBcInfo", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetBcInfo(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetGpuClocks", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetGpuClocks(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetHiWater", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetHiWater(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetStatus", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetStatus(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetTraceCounter", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetTraceCounter(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetTraceWptr", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetTraceWptr(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetWrapCounts", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetWrapCounts(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetWrapCounts2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetWrapCounts2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttGetWritebackLabels", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttGetWritebackLabels(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttInit", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSelectMode", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSelectMode(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSelectTarget", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSelectTarget(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSelectTokens", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSelectTokens(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetCuPerfMask", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetCuPerfMask(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetDceEventWrite", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetDceEventWrite(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetHiWater", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetHiWater(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetTraceBuffer2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetTraceBuffer2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetTraceBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetTraceBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSetUserData", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetUserData(CpuContext ctx) => Ok(ctx);

    // ---- Misc / debug ----

    [SysAbiExport(ExportName = "sceGnmSqttSetUserdataTimer", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSetUserdataTimer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttStartTrace", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttStartTrace(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttStopTrace", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttStopTrace(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSwitchTraceBuffer", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSwitchTraceBuffer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttSwitchTraceBuffer2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttSwitchTraceBuffer2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSqttWaitForEvent", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSqttWaitForEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSubmitAndFlipCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSubmitAndFlipCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSubmitAndFlipCommandBuffersForWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSubmitAndFlipCommandBuffersForWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSubmitCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSubmitCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSubmitCommandBuffersForWorkload", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSubmitCommandBuffersForWorkload(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSubmitDone", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSubmitDone(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysClose", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysClose(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysEnableSubmitDone45Exception", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysEnableSubmitDone45Exception(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysGetClientNumber", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysGetClientNumber(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysIsGameClosed", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysIsGameClosed(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysOpen", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysOpen(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysResetOttvLibrary", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysResetOttvLibrary(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysSubmitCommandBuffersWithPid", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysSubmitCommandBuffersWithPid(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmSysSubmitFlipHandleProxy", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmSysSubmitFlipHandleProxy(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUnmapComputeQueue", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUnmapComputeQueue(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUnregisterAllResourcesForOwner", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUnregisterAllResourcesForOwner(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUnregisterOwnerAndResources", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUnregisterOwnerAndResources(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUnregisterResource", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUnregisterResource(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUpdateGsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUpdateGsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUpdateHsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUpdateHsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUpdatePsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUpdatePsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUpdatePsShader350", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUpdatePsShader350(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmUpdateVsShader", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmUpdateVsShader(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateAndKickCommandBuffer", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateAndKickCommandBuffer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateAndKickCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateAndKickCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateAndSubmitCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateAndSubmitCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateCommandBuffer", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateCommandBuffer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateDisableDiagnostics", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateDisableDiagnostics(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateDisableDiagnostics2", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateDisableDiagnostics2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateDispatchCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateDispatchCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateDrawCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateDrawCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateGetDiagnosticInfo", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateGetDiagnosticInfo(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateGetDiagnostics", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateGetDiagnostics(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateGetVersion", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateGetVersion(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateOnSubmitEnabled", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateOnSubmitEnabled(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateResetState", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateResetState(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidateSubmitAndFlipCommandBuffers", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidateSubmitAndFlipCommandBuffers(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmValidationRegisterMemoryCheckCallback", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmValidationRegisterMemoryCheckCallback(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmWaitVBlank", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmWaitVBlank(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmApplyCheckerboardResolve", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmApplyCheckerboardResolve(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(ExportName = "sceGnmApplyGeometryResolve", Target = Generation.Gen5, LibraryName = "libSceGnmDriver")]
    public static int sceGnmApplyGeometryResolve(CpuContext ctx) => Ok(ctx);

}

