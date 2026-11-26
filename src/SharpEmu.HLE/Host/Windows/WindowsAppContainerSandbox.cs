// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Opt-in sandbox for the emulator child (<c>SHARPEMU_SANDBOX=1</c>). The child runs in an AppContainer with no
/// capabilities, so a malicious game dump gets no more than the folders of a <see cref="SandboxPlan"/>, no network
/// and no second process. The launchers add <see cref="AddSecurityCapabilities"/> and <see cref="AddHandleList"/> to
/// the attribute list they already build for the mitigation policy, pass <see cref="CreateEnvironmentBlock"/> to
/// CreateProcessW and put the process in the job <see cref="CreateConfinedJob"/> returns.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsAppContainerSandbox : IDisposable
{
    public const string EnableVariable = SandboxLaunchPolicy.Variable;
    /// <summary>Carries the launch report to the child, which prints it into its own log.</summary>
    public const string ReportVariable = "SHARPEMU_SANDBOX_REPORT";
    public const string ProfileName = "SharpEmu.Sandbox";

    /// <summary>Mitigation policy, security capabilities and handle list.</summary>
    public const int AttributeCount = 3;

    private const nint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const nint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ReadExecuteMask = 0x1200A9;  // FILE_GENERIC_READ | FILE_GENERIC_EXECUTE
    private const uint ReadWriteMask = 0x1301BF;    // FILE_GENERIC_READ | FILE_GENERIC_WRITE | DELETE | FILE_DELETE_CHILD
    private const uint AttributesMask = 0x100080;   // FILE_READ_ATTRIBUTES | SYNCHRONIZE
    private const int AlreadyExists = unchecked((int)0x800700B7);
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int TokenIsAppContainer = 29;
    private const uint HandleFlagInherit = 1;

    private readonly nint _sid;
    private nint _capabilities;
    private nint _handleList;
    private nint _environment;
    private nint[] _listedHandles = [];
    // The plan's inherited files, opened by the launcher; closed with the sandbox.
    private readonly List<(SafeFileHandle Handle, string Path)> _inheritedFiles;

    private WindowsAppContainerSandbox(
        SandboxPlan plan, nint sid, string sidText, List<(SafeFileHandle Handle, string Path)> inheritedFiles)
    {
        Plan = plan;
        _sid = sid;
        _inheritedFiles = inheritedFiles;
        ProfileSid = sidText;
        Report = $"AppContainer sandbox on: profile={ProfileName} sid={sidText}; grants: {plan.Describe()}";
    }

    public SandboxPlan Plan { get; }

    public string ProfileSid { get; }

    /// <summary>The one launch line: profile, SID and every granted folder with its access.</summary>
    public string Report { get; }

    /// <summary>
    /// True when the launcher sees <c>SHARPEMU_SANDBOX=1</c>. This is the command line's switch and it stays opt-in
    /// (unset = off) so scripted and development runs do not change; only the GUI defaults to the sandbox, through
    /// <see cref="SandboxLaunchPolicy"/>.
    /// </summary>
    public static bool IsRequested() =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    /// <summary>True when this process runs inside an AppContainer.</summary>
    public static bool IsCurrentProcessAppContainer()
    {
        if (!OperatingSystem.IsWindows() || !OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out var token))
        {
            return false;
        }

        try
        {
            uint value = 0;
            return GetTokenInformation(token, TokenIsAppContainer, (nint)(&value), sizeof(uint), out _) && value != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Opt-in self-test of the blocked-launch warning, honoured by the sandboxed child only.</summary>
    public const string SelfTestVariable = "SHARPEMU_SANDBOX_SELFTEST";

    /// <summary>
    /// <c>SHARPEMU_SANDBOX_SELFTEST=spawn</c>: five seconds after it is called (once the window exists), the sandboxed
    /// child tries to start <c>cmd.exe /c exit 0</c> once, so the launcher's warning (log line, balloon, sheet) can be
    /// seen on screen. Does nothing outside the AppContainer.
    /// </summary>
    public static void StartSelfTestIfRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(SelfTestVariable), "spawn", StringComparison.Ordinal) ||
            !IsCurrentProcessAppContainer())
        {
            return;
        }

        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            Console.Error.WriteLine("[SANDBOX][INFO] self-test: tried to start a process");
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"[SANDBOX][INFO] self-test: the start failed: {exception.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "SharpEmu sandbox self-test",
        }.Start();
    }

    /// <summary>
    /// Creates (or reuses) the profile and gives it the plan's folders. Throws when any part fails: a launch that
    /// asked for the sandbox must not fall back to running unconfined.
    /// </summary>
    public static WindowsAppContainerSandbox Create(SandboxInputs inputs)
    {
        if (SandboxPlan.FindMissingGame(inputs) is { } missingGame)
        {
            throw new InvalidOperationException(missingGame);
        }

        var plan = SandboxPlan.Build(inputs);
        // Before anything is created or granted: a volume without ACLs cannot be opened from the container at all.
        SandboxVolumeCheck.ThrowIfUnusable(plan.Folders, SandboxVolumeCheck.QueryVolume);

        var dataRoot = Path.GetFullPath(inputs.DataRoot);
        var sid = EnsureProfile(out var sidText);
        var inheritedFiles = new List<(SafeFileHandle Handle, string Path)>();
        try
        {
            var identity = new SecurityIdentifier(sidText);
            Directory.CreateDirectory(plan.TempDirectory);
            var granted = new List<SandboxGrantEntry>();
            var failures = new List<string>();
            RevokeBroaderGrants(plan, dataRoot, identity, failures);
            foreach (var folder in plan.Folders)
            {
                try
                {
                    if (folder.Access != SandboxAccess.ReadExecute)
                    {
                        Directory.CreateDirectory(folder.Path);
                    }

                    if (!(folder.IsFile ? File.Exists(folder.Path) : Directory.Exists(folder.Path)))
                    {
                        failures.Add($"{folder.Path} ({folder.Purpose}) does not exist");
                        continue;
                    }

                    var labeled = folder.Access == SandboxAccess.ReadWrite;
                    // The record is written first: a grant that fails half way still needs revoking.
                    granted.Add(new SandboxGrantEntry(folder.Path, folder.Access, folder.IsFile, labeled));
                    SaveRecord(dataRoot, granted);
                    Grant(folder, identity, sid);
                    if (labeled)
                    {
                        SetLowLabel(folder);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failures.Add($"{folder.Path} ({folder.Purpose}): {exception.Message}");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException("Could not prepare the sandbox folders: " + string.Join(" | ", failures));
            }

            // Files the container cannot be granted are opened here and handed down: access is checked at open time only.
            foreach (var path in plan.InheritedFiles)
            {
                var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                // Not inheritable: only InheritListedHandles makes it so, for the CreateProcessW call alone.
                inheritedFiles.Add((handle, path));
            }

            return new WindowsAppContainerSandbox(plan, sid, sidText, inheritedFiles);
        }
        catch
        {
            foreach (var (handle, _) in inheritedFiles)
            {
                handle.Dispose();
            }

            FreeSid(sid);
            throw;
        }
    }

    /// <summary>Adds PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES: the container SID and no capabilities.</summary>
    public void AddSecurityCapabilities(nint attributeList)
    {
        if (_capabilities == 0)
        {
            _capabilities = Marshal.AllocHGlobal(sizeof(SecurityCapabilities));
            *(SecurityCapabilities*)_capabilities = new SecurityCapabilities { AppContainerSid = _sid };
        }

        if (!UpdateProcThreadAttribute(
                attributeList, 0, ProcThreadAttributeSecurityCapabilities, _capabilities, (nuint)sizeof(SecurityCapabilities), 0, 0))
        {
            throw new InvalidOperationException($"Could not apply the AppContainer attribute (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Limits the handles the child inherits to the given ones (its output pipes). Without it, every inheritable
    /// handle of the launcher would reach the confined process.
    /// </summary>
    public void AddHandleList(nint attributeList, params nint[] handles)
    {
        handles = [.. handles, .. _inheritedFiles.Select(file => file.Handle.DangerousGetHandle())];
        _listedHandles = handles;
        if (_handleList == 0)
        {
            _handleList = Marshal.AllocHGlobal(sizeof(nint) * handles.Length);
        }

        for (var index = 0; index < handles.Length; index++)
        {
            ((nint*)_handleList)[index] = handles[index];
        }

        if (!UpdateProcThreadAttribute(
                attributeList, 0, ProcThreadAttributeHandleList, _handleList, (nuint)(sizeof(nint) * handles.Length), 0, 0))
        {
            throw new InvalidOperationException($"Could not restrict the inherited handles (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Makes the handles given to <see cref="AddHandleList"/> (and the opened archives) inheritable until the scope is
    /// disposed. They are created non-inheritable, so no other CreateProcess of the launcher passes them on; wrap the
    /// sandboxed CreateProcessW call in this scope.
    /// </summary>
    public IDisposable InheritListedHandles() => new HandleInheritScope(_listedHandles);

    /// <summary>Makes these handles inheritable until the scope is disposed (see <see cref="InheritListedHandles"/>).</summary>
    public static IDisposable InheritScope(params nint[] handles) => new HandleInheritScope(handles);

    /// <summary>True when the handle has HANDLE_FLAG_INHERIT set.</summary>
    public static bool IsInheritable(nint handle) =>
        GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0;

    private sealed class HandleInheritScope : IDisposable
    {
        private readonly List<nint> _set = [];

        public HandleInheritScope(IEnumerable<nint> handles)
        {
            foreach (var handle in handles)
            {
                if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
                {
                    var error = Marshal.GetLastWin32Error();
                    Dispose();
                    throw new InvalidOperationException($"Could not make a handle inheritable for the sandboxed child (Win32 error {error}).");
                }

                _set.Add(handle);
            }
        }

        public void Dispose()
        {
            foreach (var handle in _set)
            {
                _ = SetHandleInformation(handle, HandleFlagInherit, 0);
            }

            _set.Clear();
        }
    }

    /// <summary>
    /// The child's environment: the launcher's current one, the plan's folders, and the launch report. Call it when
    /// the other variables for the child are set; the block is freed with the sandbox. The extra variables are the
    /// launcher's per-launch values for the child (the gamepad bridge's section handle). Every variable of the
    /// launcher reaches the child, <c>SHARPEMU_SANDBOX_SELFTEST</c> included.
    /// </summary>
    public nint CreateEnvironmentBlock(IEnumerable<KeyValuePair<string, string>>? extraVariables = null)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in Plan.Environment)
        {
            variables[name] = value;
        }

        foreach (var (name, value) in extraVariables ?? [])
        {
            variables[name] = value;
        }

        variables[ReportVariable] = Report;
        if (_inheritedFiles.Count != 0)
        {
            variables[InheritedFiles.Variable] = InheritedFiles.Format(
                _inheritedFiles.Select(file => (file.Handle.DangerousGetHandle(), file.Path)));
        }
        else
        {
            variables.Remove(InheritedFiles.Variable);
        }

        var text = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        text.Append('\0');
        if (_environment != 0)
        {
            Marshal.FreeHGlobal(_environment);
        }

        _environment = Marshal.StringToHGlobalUni(text.ToString());
        return _environment;
    }

    /// <summary>
    /// Sets the job limits of a sandboxed child: kill on close and one process, so the guest cannot start another.
    /// </summary>
    public bool TryConfigureJob(nint job)
    {
        var info = new JobObjectExtendedLimitInformation();
        info.Basic.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess;
        info.Basic.ActiveProcessLimit = 1;
        return SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, (nint)(&info), (uint)sizeof(JobObjectExtendedLimitInformation));
    }

    /// <summary>
    /// A job holding the process, with the sandbox limits. Throws (after killing the process) on failure so that the
    /// guest never runs without the limit.
    /// </summary>
    /// <remarks>
    /// Attaches a <see cref="SandboxJobMonitor"/> before the process joins the job. The monitor is null when the completion
    /// port could not be set up; the caller disposes it before it closes the job.
    /// </remarks>
    public nint CreateConfinedJob(nint process, out SandboxJobMonitor? monitor)
    {
        monitor = null;
        var job = CreateJobObjectW(0, null);
        var configured = job != 0 && TryConfigureJob(job);
        if (configured)
        {
            monitor = SandboxJobMonitor.Attach(job);
        }

        if (!configured || !AssignProcessToJobObject(job, process))
        {
            var error = Marshal.GetLastWin32Error();
            monitor?.Dispose();
            monitor = null;
            if (job != 0)
            {
                CloseHandle(job);
            }

            _ = TerminateProcess(process, 1);
            throw new InvalidOperationException($"Could not confine the emulator process to its job (Win32 error {error}).");
        }

        return job;
    }

    /// <summary>
    /// A pipe for the child's output: the write end goes to the child. Neither end is inheritable until
    /// <see cref="InheritListedHandles"/> covers the CreateProcessW call.
    /// </summary>
    public static bool TryCreateOutputPipe(out nint readHandle, out nint writeHandle)
    {
        var attributes = new SecurityAttributes { Size = sizeof(SecurityAttributes), InheritHandle = 0 };
        if (!CreatePipe(out readHandle, out writeHandle, ref attributes, 0))
        {
            return false;
        }

        return SetHandleInformation(readHandle, HandleFlagInherit, 0);
    }

    /// <summary>Reads the pipe on a background thread, one line at a time, until the child closes it.</summary>
    public static Thread PumpOutput(nint readHandle, Action<string> onLine)
    {
        var thread = new Thread(() =>
        {
            using var handle = new SafeFileHandle(readHandle, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                onLine(line);
            }
        }, 256 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu sandbox output reader",
        };
        thread.Start();
        return thread;
    }

    public void Dispose()
    {
        if (_capabilities != 0)
        {
            Marshal.FreeHGlobal(_capabilities);
            _capabilities = 0;
        }

        if (_handleList != 0)
        {
            Marshal.FreeHGlobal(_handleList);
            _handleList = 0;
        }

        if (_environment != 0)
        {
            Marshal.FreeHGlobal(_environment);
            _environment = 0;
        }

        foreach (var (handle, _) in _inheritedFiles)
        {
            handle.Dispose();
        }

        _inheritedFiles.Clear();
        FreeSid(_sid);
    }

    /// <summary>
    /// <c>--sandbox-cleanup</c>: revokes every entry the recorded launches added, restores the labels, deletes the
    /// profile and the record. Returns the process exit code.
    /// </summary>
    public static int Cleanup(string dataRoot, TextWriter output)
    {
        var failed = false;
        var recordPath = SandboxGrantRecord.FilePath(dataRoot);
        if (DeriveAppContainerSidFromAppContainerName(ProfileName, out var sid) != 0 || sid == 0)
        {
            output.WriteLine($"[SANDBOX][WARN] Profile {ProfileName} has no SID to revoke.");
            return 1;
        }

        try
        {
            var identity = new SecurityIdentifier(SidToString(sid));
            if (File.Exists(recordPath))
            {
                var record = SandboxGrantRecord.FromJson(File.ReadAllText(recordPath));
                foreach (var entry in record.Entries)
                {
                    if (!(entry.IsFile ? File.Exists(entry.Path) : Directory.Exists(entry.Path)))
                    {
                        output.WriteLine($"[SANDBOX][INFO] Gone, nothing to revoke: {entry.Path}");
                        continue;
                    }

                    try
                    {
                        Revoke(entry, identity);
                        if (entry.Labeled)
                        {
                            ClearLabel(entry);
                        }

                        output.WriteLine($"[SANDBOX][INFO] Revoked {entry.Access}{(entry.Labeled ? " and the Low label" : string.Empty)}: {entry.Path}");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        failed = true;
                        output.WriteLine($"[SANDBOX][ERROR] Could not revoke {entry.Path}: {exception.Message}");
                    }
                }
            }
            else
            {
                output.WriteLine($"[SANDBOX][INFO] No grant record at {recordPath}.");
            }
        }
        finally
        {
            FreeSid(sid);
        }

        var result = DeleteAppContainerProfile(ProfileName);
        output.WriteLine(result == 0 || result == unchecked((int)0x80070490) /* HRESULT_FROM_WIN32(ERROR_NOT_FOUND) */
            ? $"[SANDBOX][INFO] Profile {ProfileName} deleted."
            : $"[SANDBOX][ERROR] DeleteAppContainerProfile failed: 0x{result:X8}");
        failed |= result != 0 && result != unchecked((int)0x80070490);
        if (!failed)
        {
            try
            {
                File.Delete(recordPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"[SANDBOX][WARN] Could not delete {recordPath}: {exception.Message}");
            }
        }

        return failed ? 1 : 0;
    }

    // A grant of a whole folder from an earlier launch (the user folder, before saves were granted per game) would
    // still expose everything this plan keeps out of the child's reach: revoke the ones that contain a folder the
    // plan grants separately, and forget them in the record.
    private static void RevokeBroaderGrants(
        SandboxPlan plan, string dataRoot, SecurityIdentifier identity, List<string> failures)
    {
        var candidates = new List<string> { Path.Combine(dataRoot, "user") };
        var recordPath = SandboxGrantRecord.FilePath(dataRoot);
        if (File.Exists(recordPath))
        {
            try
            {
                candidates.AddRange(SandboxGrantRecord.FromJson(File.ReadAllText(recordPath)).Entries
                    .Where(entry => !entry.IsFile && entry.Access == SandboxAccess.ReadWrite)
                    .Select(entry => entry.Path));
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        foreach (var candidate in candidates.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (plan.Folders.Any(folder => !folder.IsFile && string.Equals(folder.Path, candidate, StringComparison.OrdinalIgnoreCase)) ||
                !plan.Folders.Any(folder => folder.Access == SandboxAccess.ReadWrite && SandboxPlan.IsUnder(candidate, folder.Path)) ||
                !Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                var revoked = false;
                if (HasEntry(candidate, false, identity, 0))
                {
                    var entry = new SandboxGrantEntry(candidate, SandboxAccess.ReadWrite, false, true);
                    Revoke(entry, identity);
                    ClearLabel(entry);
                    revoked = true;
                }

                if (File.Exists(recordPath))
                {
                    File.WriteAllText(recordPath, SandboxGrantRecord.FromJson(File.ReadAllText(recordPath)).Without(candidate).ToJson());
                }

                if (revoked)
                {
                    Console.Error.WriteLine($"[SANDBOX][INFO] Revoked the earlier grant of the whole folder {candidate}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                failures.Add($"{candidate} (earlier whole-folder grant): {exception.Message}");
            }
        }
    }

    private static void SaveRecord(string dataRoot, IReadOnlyList<SandboxGrantEntry> added)
    {
        var path = SandboxGrantRecord.FilePath(dataRoot);
        var record = new SandboxGrantRecord(ProfileName, []);
        if (File.Exists(path))
        {
            try
            {
                record = SandboxGrantRecord.FromJson(File.ReadAllText(path));
            }
            catch (System.Text.Json.JsonException)
            {
                // An unreadable record is rewritten from this launch; cleanup then revokes what is listed.
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, record.Merge(added).ToJson());
    }

    private static nint EnsureProfile(out string sidText)
    {
        var result = CreateAppContainerProfile(ProfileName, "SharpEmu Sandbox", "Confines the SharpEmu game process", 0, 0, out var sid);
        if (result == AlreadyExists)
        {
            result = DeriveAppContainerSidFromAppContainerName(ProfileName, out sid);
        }

        if (result != 0 || sid == 0)
        {
            throw new InvalidOperationException($"Could not create the AppContainer profile {ProfileName}: HRESULT 0x{result:X8}");
        }

        sidText = SidToString(sid);
        return sid;
    }

    // Read access for the emulator folder, game folder and add-on content; read/write/delete for the folders it
    // keeps data in. A folder that already carries the entry is left alone: setting the ACL walks every file below
    // it, which on a game dump or a cache of gigabytes takes a while.
    private static void Grant(SandboxFolder folder, SecurityIdentifier identity, nint sid)
    {
        var mask = folder.Access switch
        {
            SandboxAccess.ReadWrite => ReadWriteMask,
            SandboxAccess.AttributesOnly => AttributesMask,
            _ => ReadExecuteMask,
        };
        var inherit = folder.Access != SandboxAccess.AttributesOnly;
        if (HasEntry(folder.Path, folder.IsFile, identity, mask, inherit))
        {
            return;
        }

        ModifyDacl(folder.Path, folder.IsFile, sid, mask, grant: true, inherit);
    }

    private static void Revoke(SandboxGrantEntry entry, SecurityIdentifier identity)
    {
        if (!HasEntry(entry.Path, entry.IsFile, identity, 0))
        {
            return;
        }

        var sid = ConvertSid(identity);
        try
        {
            ModifyDacl(entry.Path, entry.IsFile, sid, 0, grant: false);
        }
        finally
        {
            LocalFree(sid);
        }
    }

    private static bool HasEntry(string path, bool isFile, SecurityIdentifier identity, uint mask, bool inherit = true)
    {
        FileSystemSecurity security = isFile
            ? new FileInfo(path).GetAccessControl(AccessControlSections.Access)
            : new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference != identity || rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (mask == 0)
            {
                return true;
            }

            var inherits = isFile ||
                (inherit
                    ? rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                        rule.PropagationFlags == PropagationFlags.None
                    : rule.InheritanceFlags == InheritanceFlags.None);
            if (inherits && ((uint)rule.FileSystemRights & mask) == mask)
            {
                return true;
            }
        }

        return false;
    }

    private static void ModifyDacl(string path, bool isFile, nint sid, uint mask, bool grant, bool inherit = true)
    {
        const int SeFileObject = 1;
        const uint DaclSecurityInformation = 4;
        const uint UnprotectedDacl = 0x20000000;
        var error = GetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation, 0, 0, out var oldDacl, 0, out var descriptor);
        if (error != 0)
        {
            throw new InvalidOperationException($"reading the ACL failed (Win32 error {error})");
        }

        try
        {
            var access = new ExplicitAccess
            {
                Permissions = mask,
                Mode = grant ? 1 /* GRANT_ACCESS */ : 4 /* REVOKE_ACCESS */,
                Inheritance = isFile || !inherit ? 0u : 3u /* SUB_CONTAINERS_AND_OBJECTS_INHERIT */,
                Trustee = new Trustee { Form = 0 /* TRUSTEE_IS_SID */, Name = sid },
            };
            error = SetEntriesInAclW(1, ref access, oldDacl, out var newDacl);
            if (error != 0)
            {
                throw new InvalidOperationException($"building the ACL failed (Win32 error {error})");
            }

            try
            {
                error = SetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation | UnprotectedDacl, 0, 0, newDacl, 0);
                if (error != 0)
                {
                    throw new InvalidOperationException($"writing the ACL failed (Win32 error {error})");
                }
            }
            finally
            {
                LocalFree(newDacl);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    // A container (Low integrity) token cannot write what carries a higher label, so a writable folder needs a Low
    // label besides the ACL entry. The label is inherited by what is created there afterwards; files that exist
    // already are labelled one by one when the folder's own label is new.
    private static void SetLowLabel(SandboxFolder folder)
    {
        if (HasLowLabel(folder.Path))
        {
            return;
        }

        WriteLabel(folder.Path, folder.IsFile, "S:(ML;OICI;NW;;;LW)", "S:(ML;;NW;;;LW)");
        if (folder.IsFile)
        {
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(folder.Path, "*", SearchOption.AllDirectories))
        {
            if (!HasLowLabel(child))
            {
                WriteLabel(child, !Directory.Exists(child), "S:(ML;OICI;NW;;;LW)", "S:(ML;;NW;;;LW)");
            }
        }
    }

    private static void ClearLabel(SandboxGrantEntry entry)
    {
        // The default label of a file is Medium with no write-up; an explicit one reads the same to the system.
        WriteLabel(entry.Path, entry.IsFile, "S:(ML;OICI;NW;;;ME)", "S:(ML;;NW;;;ME)");
        if (entry.IsFile)
        {
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path, "*", SearchOption.AllDirectories))
        {
            if (HasLowLabel(child))
            {
                WriteLabel(child, !Directory.Exists(child), "S:(ML;OICI;NW;;;ME)", "S:(ML;;NW;;;ME)");
            }
        }
    }

    private static void WriteLabel(string path, bool isFile, string directorySddl, string fileSddl)
    {
        const uint LabelSecurityInformation = 0x10;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(isFile ? fileSddl : directorySddl, 1, out var descriptor, 0))
        {
            throw new InvalidOperationException($"building the label failed (Win32 error {Marshal.GetLastWin32Error()})");
        }

        try
        {
            GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _);
            var error = SetNamedSecurityInfoW(path, 1, LabelSecurityInformation, 0, 0, 0, sacl);
            if (error != 0)
            {
                throw new InvalidOperationException($"setting the integrity label of {path} failed (Win32 error {error})");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static bool HasLowLabel(string path)
    {
        const uint LabelSecurityInformation = 0x10;
        if (GetNamedSecurityInfoLabelW(path, 1, LabelSecurityInformation, 0, 0, 0, out _, out var descriptor) != 0)
        {
            return false;
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, LabelSecurityInformation, out var text, 0))
            {
                return false;
            }

            var sddl = Marshal.PtrToStringUni(text) ?? string.Empty;
            LocalFree(text);
            return sddl.Contains(";;;LW)", StringComparison.Ordinal);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string SidToString(nint sid)
    {
        if (!ConvertSidToStringSidW(sid, out var text))
        {
            throw new InvalidOperationException("Could not format the AppContainer SID.");
        }

        var value = Marshal.PtrToStringUni(text) ?? string.Empty;
        LocalFree(text);
        return value;
    }

    private static nint ConvertSid(SecurityIdentifier identity)
    {
        if (!ConvertStringSidToSidW(identity.Value, out var sid))
        {
            throw new InvalidOperationException($"Could not convert the SID {identity.Value}.");
        }

        return sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Trustee
    {
        public nint Multiple;
        public int MultipleOperation;
        public int Form;
        public int Type;
        public nint Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplicitAccess
    {
        public uint Permissions;
        public int Mode;
        public uint Inheritance;
        public Trustee Trustee;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint capabilityCount, out nint sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string name);

    [DllImport("advapi32.dll")]
    private static extern nint FreeSid(nint sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(nint sid, out nint text);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string text, out nint sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfoW(string path, int objectType, uint information, nint owner, nint group, out nint dacl, nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetNamedSecurityInfoW")]
    private static extern uint GetNamedSecurityInfoLabelW(string path, int objectType, uint information, nint owner, nint group, nint dacl, out nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfoW(string path, int objectType, uint information, nint owner, nint group, nint dacl, nint sacl);

    [DllImport("advapi32.dll")]
    private static extern uint SetEntriesInAclW(uint count, ref ExplicitAccess entries, nint oldAcl, out nint newAcl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass, nint information, uint length, out uint returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint descriptor, uint revision, uint information, out nint text, nint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(nint descriptor, out int present, out nint sacl, out int defaulted);
}
