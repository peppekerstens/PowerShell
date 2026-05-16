// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if UNIX

using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.ServiceProcess;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

#nullable enable

namespace Microsoft.PowerShell.Commands
{
    // -------------------------------------------------------------------------
    // D-Bus constants
    // -------------------------------------------------------------------------

    internal static class SystemdDBus
    {
        internal const string Destination  = "org.freedesktop.systemd1";
        internal const string ManagerPath  = "/org/freedesktop/systemd1";
        internal const string ManagerIface = "org.freedesktop.systemd1.Manager";
        internal const string UnitIface    = "org.freedesktop.systemd1.Unit";
        internal const string PropsIface   = "org.freedesktop.DBus.Properties";

        // ListUnits reply element indices  (ssssssouso)
        internal const int LU_Name        = 0;
        internal const int LU_Desc        = 1;
        internal const int LU_LoadState   = 2;
        internal const int LU_ActiveState = 3;
        internal const int LU_SubState    = 4;
    }

    // -------------------------------------------------------------------------
    // Public output type
    // -------------------------------------------------------------------------

    /// <summary>
    /// Represents a systemd service unit.  Mirrors the most-used properties of
    /// <see cref="System.ServiceProcess.ServiceController"/> so that scripts
    /// that access <c>$svc.Status</c>, <c>$svc.Name</c>, etc. work unchanged.
    /// </summary>
    public sealed class LinuxServiceInfo
    {
        /// <summary>The systemd unit name, e.g. <c>sshd.service</c>.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>Human-readable description from the unit file.</summary>
        public string DisplayName { get; internal set; } = string.Empty;

        /// <summary>Service status mapped from systemd <c>ActiveState</c>.</summary>
        public ServiceControllerStatus Status { get; internal set; }

        /// <summary>Start-up type mapped from <c>UnitFileState</c>.</summary>
        public ServiceStartupType StartType { get; internal set; }

        /// <summary>Raw systemd <c>ActiveState</c> string.</summary>
        public string ActiveState { get; internal set; } = string.Empty;

        /// <summary>Raw systemd <c>SubState</c> string.</summary>
        public string SubState { get; internal set; } = string.Empty;

        internal static ServiceControllerStatus MapStatus(string activeState, string subState)
            => activeState switch
            {
                "active"      => subState == "running"
                                    ? ServiceControllerStatus.Running
                                    : ServiceControllerStatus.StartPending,
                "activating"  => ServiceControllerStatus.StartPending,
                "deactivating"=> ServiceControllerStatus.StopPending,
                "reloading"   => ServiceControllerStatus.ContinuePending,
                _             => ServiceControllerStatus.Stopped,  // failed / inactive / unknown
            };

        internal static ServiceStartupType MapStartupType(string unitFileState)
            => unitFileState switch
            {
                "enabled"         => ServiceStartupType.Automatic,
                "enabled-runtime" => ServiceStartupType.Automatic,
                "static"          => ServiceStartupType.Manual,
                "disabled"        => ServiceStartupType.Disabled,
                "masked"          => ServiceStartupType.Disabled,
                _                 => ServiceStartupType.Manual,
            };
    }

    // -------------------------------------------------------------------------
    // D-Bus helper (no process spawning)
    // -------------------------------------------------------------------------

    /// <summary>
    /// All systemd interactions use the system D-Bus via
    /// <see cref="Tmds.DBus.Protocol"/> — no <c>systemctl</c> subprocess is
    /// spawned.  Each public method opens a connection, executes the call, and
    /// closes the connection.  Connections are cheap (Unix-domain socket) and
    /// PS cmdlets are short-lived, so pooling is unnecessary.
    /// </summary>
    internal static class SystemdHelper
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private static DBusConnection OpenSystem()
        {
            var conn = new DBusConnection(DBusAddress.System!);
            conn.ConnectAsync().GetAwaiter().GetResult();
            return conn;
        }

        /// <summary>
        /// Append <c>.service</c> if the name contains no dot.
        /// </summary>
        internal static string ResolveUnitName(string name)
            => name.Contains('.') ? name : name + ".service";

        // ── ListUnits ────────────────────────────────────────────────────────

        /// <summary>
        /// Return all service units, optionally filtered by name patterns.
        /// Reads <c>ListUnits()</c> + <c>ListUnitFiles()</c> over D-Bus.
        /// </summary>
        internal static IEnumerable<LinuxServiceInfo> GetServices(string[]? namePatterns)
            => GetServicesAsync(namePatterns).GetAwaiter().GetResult();

        private static async Task<List<LinuxServiceInfo>> GetServicesAsync(string[]? namePatterns)
        {
            using var conn = OpenSystem();

            // ── 1. ListUnits — currently loaded/active units ─────────────────
            // Method signature: ListUnits() → a(ssssssouso)
            var activeUnits = new Dictionary<string, LinuxServiceInfo>(StringComparer.OrdinalIgnoreCase);

            var msg1 = BuildCall(conn, "ListUnits");
            {
                var reply = await conn.CallMethodAsync(msg1, static (Message m, object? _) =>
                {
                    var list = new List<(string name, string desc, string active, string sub)>();
                    var reader = m.GetBodyReader();
                    var arrayEnd = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(arrayEnd))
                    {
                        reader.AlignStruct();
                        string name   = reader.ReadString();
                        string desc   = reader.ReadString();
                        /*loadState*/   reader.ReadString();
                        string active = reader.ReadString();
                        string sub    = reader.ReadString();
                        /*following*/  reader.ReadString();
                        /*unitPath*/   reader.ReadObjectPath();
                        /*jobId*/      reader.ReadUInt32();
                        /*jobType*/    reader.ReadString();
                        /*jobPath*/    reader.ReadObjectPath();
                        if (name.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
                            list.Add((name, desc, active, sub));
                    }
                    return list;
                });

                foreach (var (name, desc, active, sub) in reply)
                {
                    activeUnits[name] = new LinuxServiceInfo
                    {
                        Name        = name,
                        DisplayName = desc,
                        ActiveState = active,
                        SubState    = sub,
                        Status      = LinuxServiceInfo.MapStatus(active, sub),
                        StartType   = ServiceStartupType.Manual, // overwritten below
                    };
                }
            }

            // ── 2. ListUnitFiles — startup type for all units (incl. inactive) ─
            // Method signature: ListUnitFiles() → a(ss)   (unitFile, state)
            var msg2 = BuildCall(conn, "ListUnitFiles");
            {
                var reply = await conn.CallMethodAsync(msg2, static (Message m, object? _) =>
                {
                    var list = new List<(string file, string state)>();
                    var reader = m.GetBodyReader();
                    var arrayEnd = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(arrayEnd))
                    {
                        reader.AlignStruct();
                        string file  = reader.ReadString();
                        string state = reader.ReadString();
                        if (file.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
                            list.Add((file, state));
                    }
                    return list;
                });

                foreach (var (file, state) in reply)
                {
                    // file may be a full path like /usr/lib/systemd/system/sshd.service
                    string unitName = System.IO.Path.GetFileName(file);
                    var startType = LinuxServiceInfo.MapStartupType(state);
                    if (activeUnits.TryGetValue(unitName, out var existing))
                    {
                        existing.StartType = startType;
                    }
                    else
                    {
                        // Unit exists but is not currently loaded — show as stopped
                        activeUnits[unitName] = new LinuxServiceInfo
                        {
                            Name        = unitName,
                            DisplayName = unitName,
                            ActiveState = "inactive",
                            SubState    = "dead",
                            Status      = ServiceControllerStatus.Stopped,
                            StartType   = startType,
                        };
                    }
                }
            }

            // ── 3. Apply name filter ─────────────────────────────────────────
            var result = new List<LinuxServiceInfo>();
            foreach (var svc in activeUnits.Values)
            {
                if (MatchesPatterns(svc, namePatterns))
                    result.Add(svc);
            }
            return result;
        }

        private static bool MatchesPatterns(LinuxServiceInfo svc, string[]? patterns)
        {
            if (patterns is null || patterns.Length == 0) return true;
            foreach (var pattern in patterns)
            {
                if (WildcardPattern.ContainsWildcardCharacters(pattern))
                {
                    var wp = new WildcardPattern(pattern, WildcardOptions.IgnoreCase);
                    if (wp.IsMatch(svc.Name) || wp.IsMatch(svc.DisplayName))
                        return true;
                }
                else
                {
                    string bare = pattern.EndsWith(".service", StringComparison.OrdinalIgnoreCase)
                        ? pattern : pattern + ".service";
                    if (svc.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                        || svc.Name.Equals(bare,    StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // ── StartUnit / StopUnit / RestartUnit ───────────────────────────────

        /// <summary>
        /// Calls <c>StartUnit(name, "replace")</c> on the system bus.
        /// Returns the job object path (ignored by callers that don't wait).
        /// </summary>
        internal static void StartUnit(string unitName)
            => UnitActionAsync("StartUnit", unitName).GetAwaiter().GetResult();

        /// <summary>Calls <c>StopUnit(name, "replace")</c>.</summary>
        internal static void StopUnit(string unitName)
            => UnitActionAsync("StopUnit", unitName).GetAwaiter().GetResult();

        /// <summary>Calls <c>RestartUnit(name, "replace")</c>.</summary>
        internal static void RestartUnit(string unitName)
            => UnitActionAsync("RestartUnit", unitName).GetAwaiter().GetResult();

        private static async Task UnitActionAsync(string method, string unitName)
        {
            using var conn = OpenSystem();
            var msg = BuildCallSS(conn, method, unitName, "replace");
            // Returns object path (job) — we discard it
            await conn.CallMethodAsync(msg, static (Message m, object? _) =>
                m.GetBodyReader().ReadObjectPath());
        }

        // ── EnableUnitFiles / DisableUnitFiles ────────────────────────────────

        /// <summary>
        /// Calls <c>EnableUnitFiles(names, runtime:false, force:false)</c>.
        /// Requires root / polkit authorisation.
        /// </summary>
        internal static void EnableUnits(string[] unitNames)
            => EnableUnitsAsync(unitNames).GetAwaiter().GetResult();

        private static async Task EnableUnitsAsync(string[] unitNames)
        {
            using var conn = OpenSystem();
            var msgEnable = BuildEnableMessage(conn, unitNames, runtime: false, force: false);
            // Returns (b carriesInstall, a(sss) changes) — discard
            try
            {
                await conn.CallMethodAsync(msgEnable, static (Message m, object? _) => 0).ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex) when (ex.Message.Contains("InteractiveAuthorizationRequired"))
            {
                throw new InvalidOperationException(
                    "EnableUnitFiles failed: root privileges are required. Use 'sudo pwsh'.", ex);
            }
        }

        /// <summary>
        /// Calls <c>DisableUnitFiles(names, runtime:false)</c>.
        /// Requires root / polkit authorisation.
        /// </summary>
        internal static void DisableUnits(string[] unitNames)
            => DisableUnitsAsync(unitNames).GetAwaiter().GetResult();

        private static async Task DisableUnitsAsync(string[] unitNames)
        {
            using var conn = OpenSystem();
            var msgDisable = BuildDisableMessage(conn, unitNames, runtime: false);
            try
            {
                await conn.CallMethodAsync(msgDisable, static (Message m, object? _) => 0).ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex) when (ex.Message.Contains("InteractiveAuthorizationRequired"))
            {
                throw new InvalidOperationException(
                    "DisableUnitFiles failed: root privileges are required. Use 'sudo pwsh'.", ex);
            }
        }

        // ── DaemonReload ──────────────────────────────────────────────────────

        /// <summary>
        /// Calls <c>Reload</c> on the systemd manager (equivalent to
        /// <c>systemctl daemon-reload</c>).
        /// </summary>
        internal static void DaemonReload()
        {
            using var conn = OpenSystem();
            var msg = BuildCall(conn, "Reload");
            try
            {
                conn.CallMethodAsync(msg, static (Message m, object? _) => 0)
                    .GetAwaiter().GetResult();
            }
            catch (DBusExceptionBase ex) when (ex.Message.Contains("InteractiveAuthorizationRequired"))
            {
                throw new InvalidOperationException(
                    "DaemonReload failed: root privileges are required. Use 'sudo pwsh'.", ex);
            }
        }

        // ── Unit file management ──────────────────────────────────────────────

        /// <summary>
        /// Write a .service unit file to the system or user unit directory.
        /// </summary>
        internal static void WriteUnitFile(string unitName, string description, string execStart)
        {
            string unitDir = IsNonRoot() ? GetUserUnitDir() : "/etc/systemd/system/";
            System.IO.Directory.CreateDirectory(unitDir);
            string unitPath = System.IO.Path.Combine(unitDir, unitName);
            var lines = new[]
            {
                "[Unit]",
                $"Description={description}",
                "",
                "[Service]",
                $"ExecStart={execStart}",
                "Restart=no",
                "",
                "[Install]",
                "WantedBy=multi-user.target"
            };
            System.IO.File.WriteAllLines(unitPath, lines);
        }

        /// <summary>
        /// Delete a .service unit file from the system or user unit directory.
        /// Does nothing if the file does not exist.
        /// </summary>
        internal static void RemoveUnitFile(string unitName)
        {
            string unitDir = IsNonRoot() ? GetUserUnitDir() : "/etc/systemd/system/";
            string unitPath = System.IO.Path.Combine(unitDir, unitName);
            if (System.IO.File.Exists(unitPath))
                System.IO.File.Delete(unitPath);
        }

        private static bool IsNonRoot()
        {
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "id",
                    Arguments = "-u",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                })!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return !output.Trim().Equals("0");
            }
            catch { return true; }
        }

        private static string GetUserUnitDir()
        {
            string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? System.IO.Path.Combine(
                    Environment.GetEnvironmentVariable("HOME") ?? "/root",
                    ".config");
            return System.IO.Path.Combine(configHome, "systemd", "user");
        }

        // ── Message builders ─────────────────────────────────────────────────

        /// <summary>Build a no-argument Manager method call.</summary>
        private static MessageBuffer BuildCall(DBusConnection conn, string member)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: SystemdDBus.Destination,
                path:        SystemdDBus.ManagerPath,
                @interface:  SystemdDBus.ManagerIface,
                member:      member);
            return writer.CreateMessage();
        }

        /// <summary>Build a (ss) Manager method call.</summary>
        private static MessageBuffer BuildCallSS(DBusConnection conn, string member, string arg1, string arg2)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: SystemdDBus.Destination,
                path:        SystemdDBus.ManagerPath,
                @interface:  SystemdDBus.ManagerIface,
                signature:   "ss",
                member:      member);
            writer.WriteString(arg1);
            writer.WriteString(arg2);
            return writer.CreateMessage();
        }

        /// <summary>Build an EnableUnitFiles (asbb) call.</summary>
        private static MessageBuffer BuildEnableMessage(
            DBusConnection conn, string[] files, bool runtime, bool force)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: SystemdDBus.Destination,
                path:        SystemdDBus.ManagerPath,
                @interface:  SystemdDBus.ManagerIface,
                signature:   "asbb",
                member:      "EnableUnitFiles");
            writer.WriteArray(files);
            writer.WriteBool(runtime);
            writer.WriteBool(force);
            return writer.CreateMessage();
        }

        /// <summary>Build a DisableUnitFiles (asb) call.</summary>
        private static MessageBuffer BuildDisableMessage(
            DBusConnection conn, string[] files, bool runtime)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: SystemdDBus.Destination,
                path:        SystemdDBus.ManagerPath,
                @interface:  SystemdDBus.ManagerIface,
                signature:   "asb",
                member:      "DisableUnitFiles");
            writer.WriteArray(files);
            writer.WriteBool(runtime);
            return writer.CreateMessage();
        }
    }

    // =========================================================================
    // Cmdlets
    // =========================================================================

    #region GetServiceCommand

    /// <summary>Linux implementation of <c>Get-Service</c> backed by D-Bus.</summary>
    [Cmdlet(VerbsCommon.Get, "Service", DefaultParameterSetName = "Default",
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2096496",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class GetServiceCommand : PSCmdlet
    {
        /// <summary>Service name(s). Wildcards accepted.</summary>
        [Parameter(Position = 0, ParameterSetName = "Default",
            ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        [Alias("ServiceName")]
        public string[]? Name { get; set; }

        /// <summary>Pipeline input of existing <see cref="LinuxServiceInfo"/> objects.</summary>
        [Parameter(ParameterSetName = "InputObject", ValueFromPipeline = true)]
        public LinuxServiceInfo[]? InputObject { get; set; }

        private readonly List<string> _names = new();

        /// <inheritdoc/>
        protected override void ProcessRecord()
        {
            if (InputObject is not null)
                foreach (var s in InputObject) _names.Add(s.Name);
            else if (Name is not null)
                _names.AddRange(Name);
        }

        /// <inheritdoc/>
        protected override void EndProcessing()
        {
            string[]? patterns = _names.Count > 0 ? _names.ToArray() : null;
            try
            {
                foreach (var svc in SystemdHelper.GetServices(patterns))
                    WriteObject(svc);
            }
            catch (DBusExceptionBase ex)
            {
                WriteError(new ErrorRecord(ex, "DBusError", ErrorCategory.ResourceUnavailable, patterns));
            }
        }
    }

    #endregion GetServiceCommand

    #region ServiceUnixBase

    /// <summary>
    /// Abstract base for Start/Stop/Restart/Suspend/Resume-Service on Linux.
    /// Accepts <c>-Name</c> (positional, wildcards) or <c>-InputObject</c>.
    /// </summary>
    public abstract class ServiceUnixBase : PSCmdlet
    {
        /// <summary>Service name(s). Wildcards accepted.</summary>
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Name",
            ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string[]? Name { get; set; }

        /// <summary>Pipeline input of service objects.</summary>
        [Parameter(Mandatory = true, ParameterSetName = "InputObject",
            ValueFromPipeline = true)]
        public LinuxServiceInfo[]? InputObject { get; set; }

        /// <summary>Emit the updated service object after the operation.</summary>
        [Parameter]
        public SwitchParameter PassThru { get; set; }

        /// <inheritdoc/>
        protected override void ProcessRecord()
        {
            IEnumerable<string> unitNames;
            if (ParameterSetName == "InputObject" && InputObject is not null)
                unitNames = System.Array.ConvertAll(InputObject, s => s.Name);
            else if (Name is not null)
                unitNames = System.Array.ConvertAll(Name, SystemdHelper.ResolveUnitName);
            else
                return;

            foreach (var name in unitNames)
                OperateOnService(name);
        }

        /// <summary>Perform the unit-level operation.</summary>
        protected abstract void OperateOnService(string unitName);

        /// <summary>Re-read and emit a fresh <see cref="LinuxServiceInfo"/>.</summary>
        protected void EmitServiceInfo(string unitName)
        {
            foreach (var svc in SystemdHelper.GetServices(new[] { unitName }))
                WriteObject(svc);
        }

        /// <summary>Write a non-terminating error for a failed D-Bus call.</summary>
        protected void WriteDBusError(string unitName, string operation, Exception ex)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException(
                    $"{operation} {unitName} failed: {ex.Message}", ex),
                "DBusOperationFailed", ErrorCategory.OperationStopped, unitName));
        }
    }

    #endregion ServiceUnixBase

    #region StartServiceCommand

    /// <summary>Linux implementation of <c>Start-Service</c> via D-Bus <c>StartUnit</c>.</summary>
    [Cmdlet(VerbsLifecycle.Start, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097053",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class StartServiceCommand : ServiceUnixBase
    {
        /// <inheritdoc/>
        protected override void OperateOnService(string unitName)
        {
            if (!ShouldProcess(unitName, "Start")) return;
            try   { SystemdHelper.StartUnit(unitName); }
            catch (Exception ex) { WriteDBusError(unitName, "Start", ex); return; }
            if (PassThru) EmitServiceInfo(unitName);
        }
    }

    #endregion StartServiceCommand

    #region StopServiceCommand

    /// <summary>Linux implementation of <c>Stop-Service</c> via D-Bus <c>StopUnit</c>.</summary>
    [Cmdlet(VerbsLifecycle.Stop, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097051",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class StopServiceCommand : ServiceUnixBase
    {
        /// <inheritdoc/>
        protected override void OperateOnService(string unitName)
        {
            if (!ShouldProcess(unitName, "Stop")) return;
            try   { SystemdHelper.StopUnit(unitName); }
            catch (Exception ex) { WriteDBusError(unitName, "Stop", ex); return; }
            if (PassThru) EmitServiceInfo(unitName);
        }
    }

    #endregion StopServiceCommand

    #region RestartServiceCommand

    /// <summary>Linux implementation of <c>Restart-Service</c> via D-Bus <c>RestartUnit</c>.</summary>
    [Cmdlet(VerbsLifecycle.Restart, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2096560",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class RestartServiceCommand : ServiceUnixBase
    {
        /// <inheritdoc/>
        protected override void OperateOnService(string unitName)
        {
            if (!ShouldProcess(unitName, "Restart")) return;
            try   { SystemdHelper.RestartUnit(unitName); }
            catch (Exception ex) { WriteDBusError(unitName, "Restart", ex); return; }
            if (PassThru) EmitServiceInfo(unitName);
        }
    }

    #endregion RestartServiceCommand

    #region SuspendServiceCommand

    /// <summary>
    /// <c>Suspend-Service</c> on Linux: systemd has no pause/continue concept.
    /// Always writes a <see cref="PlatformNotSupportedException"/> error record.
    /// </summary>
    [Cmdlet(VerbsLifecycle.Suspend, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097054",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class SuspendServiceCommand : ServiceUnixBase
    {
        /// <inheritdoc/>
        protected override void OperateOnService(string unitName)
        {
            WriteError(new ErrorRecord(
                new PlatformNotSupportedException(
                    "Suspend-Service is not supported on Linux. " +
                    "systemd has no pause/continue concept for services."),
                "PlatformNotSupported", ErrorCategory.NotImplemented, unitName));
        }
    }

    #endregion SuspendServiceCommand

    #region ResumeServiceCommand

    /// <summary>
    /// <c>Resume-Service</c> on Linux: systemd has no pause/continue concept.
    /// Always writes a <see cref="PlatformNotSupportedException"/> error record.
    /// </summary>
    [Cmdlet(VerbsLifecycle.Resume, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097049",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class ResumeServiceCommand : ServiceUnixBase
    {
        /// <inheritdoc/>
        protected override void OperateOnService(string unitName)
        {
            WriteError(new ErrorRecord(
                new PlatformNotSupportedException(
                    "Resume-Service is not supported on Linux. " +
                    "systemd has no pause/continue concept for services."),
                "PlatformNotSupported", ErrorCategory.NotImplemented, unitName));
        }
    }

    #endregion ResumeServiceCommand

    #region SetServiceCommand

    /// <summary>
    /// Linux implementation of <c>Set-Service</c>.
    /// <para>
    /// Supported parameters: <c>-StartupType</c> (via <c>EnableUnitFiles</c> /
    /// <c>DisableUnitFiles</c>) and <c>-Status</c> Running/Stopped (via
    /// <c>StartUnit</c> / <c>StopUnit</c>).  Requires root or polkit
    /// authorisation for system services — same as <c>systemctl</c> itself.
    /// </para>
    /// </summary>
    [Cmdlet(VerbsCommon.Set, "Service", DefaultParameterSetName = "Name",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097055",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class SetServiceCommand : PSCmdlet
    {
        /// <summary>Service name.</summary>
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Name",
            ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string? Name { get; set; }

        /// <summary>Service object (pipeline input).</summary>
        [Parameter(Mandatory = true, ParameterSetName = "InputObject",
            ValueFromPipeline = true)]
        public LinuxServiceInfo? InputObject { get; set; }

        /// <summary>Desired startup type.</summary>
        [Parameter]
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.InvalidValue;

        /// <summary>Desired running status: <c>Running</c> or <c>Stopped</c>.</summary>
        [Parameter]
        [ValidateSet("Running", "Stopped")]
        public string? Status { get; set; }

        /// <summary>Pass the updated service object through the pipeline.</summary>
        [Parameter]
        public SwitchParameter PassThru { get; set; }

        /// <inheritdoc/>
        protected override void ProcessRecord()
        {
            string rawName  = ParameterSetName == "InputObject" ? InputObject!.Name : Name!;
            string unitName = SystemdHelper.ResolveUnitName(rawName);

            if (!ShouldProcess(unitName, "Set")) return;

            if (!Utils.IsAdministrator())
            {
                WriteError(new ErrorRecord(
                    new PSSecurityException($"{MyInvocation.MyCommand.Name} requires root privileges."),
                    "ElevationRequired", ErrorCategory.PermissionDenied, unitName));
                return;
            }

            // ── Apply startup type ────────────────────────────────────────────
            if (StartupType != ServiceStartupType.InvalidValue)
            {
                try
                {
                    if (StartupType == ServiceStartupType.Disabled)
                        SystemdHelper.DisableUnits(new[] { unitName });
                    else
                        SystemdHelper.EnableUnits(new[] { unitName });
                }
                catch (Exception ex)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"Could not change startup type for {unitName}: {ex.Message}", ex),
                        "DBusOperationFailed", ErrorCategory.OperationStopped, unitName));
                    return;
                }
            }

            // ── Apply status ──────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(Status))
            {
                try
                {
                    if (Status == "Running")
                        SystemdHelper.StartUnit(unitName);
                    else
                        SystemdHelper.StopUnit(unitName);
                }
                catch (Exception ex)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"Could not set status for {unitName}: {ex.Message}", ex),
                        "DBusOperationFailed", ErrorCategory.OperationStopped, unitName));
                    return;
                }
            }

            if (PassThru)
            {
                foreach (var svc in SystemdHelper.GetServices(new[] { unitName }))
                    WriteObject(svc);
            }
        }
    }

    #endregion SetServiceCommand

    #region NewServiceCommand

    /// <summary>
    /// Linux implementation of <c>New-Service</c>.  Writes a systemd unit file
    /// to <c>/etc/systemd/system/</c> (or the user unit directory),
    /// runs <c>daemon-reload</c>, and optionally enables the unit.
    /// Requires root or polkit authorisation for system services.
    /// </summary>
    [Cmdlet(VerbsCommon.New, "Service", SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097056",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    [OutputType(typeof(LinuxServiceInfo))]
    public sealed class NewServiceCommand : PSCmdlet
    {
        /// <summary>Name of the service to create.</summary>
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; } = string.Empty;

        /// <summary>Path to the executable (the <c>ExecStart</c> value).</summary>
        [Parameter(Mandatory = true)]
        public string BinaryPathName { get; set; } = string.Empty;

        /// <summary>Human-readable description for the unit file.</summary>
        [Parameter]
        public string Description { get; set; } = string.Empty;

        /// <summary>Startup type (enable behaviour). Defaults to <c>Manual</c>.</summary>
        [Parameter]
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.Manual;

        /// <inheritdoc/>
        protected override void ProcessRecord()
        {
            string unitName = SystemdHelper.ResolveUnitName(Name);

            if (!ShouldProcess(unitName, "Create systemd service unit")) return;

            if (!Utils.IsAdministrator())
            {
                WriteError(new ErrorRecord(
                    new PSSecurityException($"{MyInvocation.MyCommand.Name} requires root privileges."),
                    "ElevationRequired", ErrorCategory.PermissionDenied, unitName));
                return;
            }

            try
            {
                SystemdHelper.WriteUnitFile(unitName, Description, BinaryPathName);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Failed to create unit file for {unitName}: {ex.Message}", ex),
                    "UnitFileCreateFailed", ErrorCategory.WriteError, unitName));
                return;
            }

            try
            {
                SystemdHelper.DaemonReload();
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Created unit file but daemon-reload failed: {ex.Message}", ex),
                    "DaemonReloadFailed", ErrorCategory.OperationStopped, unitName));
                return;
            }

            if (StartupType == ServiceStartupType.Automatic)
            {
                try { SystemdHelper.EnableUnits(new[] { unitName }); }
                catch (Exception ex)
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"Created unit file but enable failed: {ex.Message}", ex),
                        "EnableFailed", ErrorCategory.OperationStopped, unitName));
                }
            }

            WriteObject(new LinuxServiceInfo
            {
                Name        = unitName,
                DisplayName = string.IsNullOrEmpty(Description) ? Name : Description,
                Status      = ServiceControllerStatus.Stopped,
                StartType   = StartupType,
                ActiveState = "inactive",
                SubState    = "dead",
            });
        }
    }

    #endregion NewServiceCommand

    #region RemoveServiceCommand

    /// <summary>
    /// Linux implementation of <c>Remove-Service</c>.  Stops, disables, deletes
    /// the systemd unit file, and runs <c>daemon-reload</c>.
    /// Requires root or polkit authorisation for system services.
    /// </summary>
    [Cmdlet(VerbsCommon.Remove, "Service", SupportsShouldProcess = true,
        ConfirmImpact = ConfirmImpact.High,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097052",
        RemotingCapability = RemotingCapability.SupportedByCommand)]
    public sealed class RemoveServiceCommand : PSCmdlet
    {
        /// <summary>Name of the service to remove.</summary>
        [Parameter(Mandatory = true, Position = 0,
            ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; } = string.Empty;

        /// <inheritdoc/>
        protected override void ProcessRecord()
        {
            string unitName = SystemdHelper.ResolveUnitName(Name);

            if (!ShouldProcess(unitName, "Stop, disable, and delete systemd service unit")) return;

            if (!Utils.IsAdministrator())
            {
                WriteError(new ErrorRecord(
                    new PSSecurityException($"{MyInvocation.MyCommand.Name} requires root privileges."),
                    "ElevationRequired", ErrorCategory.PermissionDenied, unitName));
                return;
            }

            try { SystemdHelper.StopUnit(unitName); }
            catch (Exception) { }

            try { SystemdHelper.DisableUnits(new[] { unitName }); }
            catch (Exception) { }

            try { SystemdHelper.RemoveUnitFile(unitName); }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Failed to remove unit file for {unitName}: {ex.Message}", ex),
                    "UnitFileRemoveFailed", ErrorCategory.WriteError, unitName));
                return;
            }

            try { SystemdHelper.DaemonReload(); }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Removed unit file but daemon-reload failed: {ex.Message}", ex),
                    "DaemonReloadFailed", ErrorCategory.OperationStopped, unitName));
            }
        }
    }

    #endregion RemoveServiceCommand
}

#endif
