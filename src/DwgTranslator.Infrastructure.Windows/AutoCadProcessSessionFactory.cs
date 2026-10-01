using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;
using DwgTranslator.Transport.Windows;

namespace DwgTranslator.Infrastructure.Windows;

public sealed record AutoCadProcessSessionOptions(
    string ExecutablePath,
    string ReadOnlyBundleDirectory,
    string WriteBundleDirectory,
    TimeSpan StartupTimeout,
    TimeSpan ExchangeTimeout,
    TimeSpan CooperativeCloseTimeout,
    CadNetloadBootstrapOptions? ReadOnlyBootstrap = null,
    CadNetloadBootstrapOptions? WriteBootstrap = null,
    TimeSpan? CleanupGraceTimeout = null)
{
    public TimeSpan EffectiveCleanupGraceTimeout => CleanupGraceTimeout ?? CooperativeCloseTimeout;
}

public sealed record CadNetloadBootstrapOptions(string AssemblyPath, string EvidenceDirectory);
public sealed record CadNetloadBootstrapArtifact(string ScriptPath, string OriginalTrustedPathsPath, string RestoredMarkerPath);

public sealed record CadBoundaryObservation(string Stage, string Operation, string? RequestFingerprint, string? HostVersion, string? PluginVersion, string? ErrorCode, ErrorCategory? ErrorCategory, bool? Retryable, string? DiagnosticId,
    Guid? JobId = null, string? RequestId = null, int? ProcessId = null, string? ProcessExecutablePath = null,
    DateTimeOffset? ProcessStartedAtUtc = null, string? ResponseHash = null, int? ExtractedCount = null,
    DateTimeOffset? ReceivedAtUtc = null, DateTimeOffset? QuitRequestedAtUtc = null,
    DateTimeOffset? ProcessExitedAtUtc = null, int? ExitCode = null, bool RequiresProcessTerminationApproval = false,
    string? TechnicalStage = null, string? NativeErrorStatus = null)
{
    public static CadBoundaryObservation Capabilities(string hostVersion) =>
        new("capabilities", MessageTypes.CapabilitiesRequest, null, hostVersion, null, null, null, null, null);
}
public interface ICadBoundaryObserver { void Observe(CadBoundaryObservation observation); }

public sealed class AutoCadProcessSessionFactory : ICadGatewaySessionFactory
{
    private const string ReadPipeVariable = "DWT_PHASE2B_PIPE";
    private const string ReadNonceVariable = "DWT_PHASE2B_NONCE";
    private const string WritePipeVariable = "DWT_PHASE4B_PIPE";
    private const string WriteNonceVariable = "DWT_PHASE4B_NONCE";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly AutoCadProcessSessionOptions _options;
    private readonly ICadBoundaryObserver? _observer;

    public AutoCadProcessSessionFactory(AutoCadProcessSessionOptions options, ICadBoundaryObserver? observer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observer = observer;
    }

    public async Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken)
    {
        Observe("open-start", CadOperationsFor(purpose), null, null);
        var configuration = ValidateConfiguration(purpose);
        if (!configuration.IsSuccess) { Observe("configuration-failed", CadOperationsFor(purpose), null, configuration.Error); return Results.Failure<ICadGatewayLease>(configuration.Error!); }
        if (Process.GetProcessesByName("acad").Length != 0 || Process.GetProcessesByName("accoreconsole").Length != 0)
            return Failure<ICadGatewayLease>("CAD_BUSY", ErrorCategory.Concurrency, "AutoCAD must be closed before starting a managed translation session.");

        var bootstrapOptions = purpose == CadSessionPurpose.ReadOnly ? _options.ReadOnlyBootstrap! : _options.WriteBootstrap!;
        var bootstrap = CadNetloadBootstrapScript.Create(bootstrapOptions);
        if (!bootstrap.IsSuccess)
        {
            Observe("bootstrap-failed", CadOperationsFor(purpose), null, bootstrap.Error);
            return Results.Failure<ICadGatewayLease>(bootstrap.Error!);
        }

        var pipeName = CadPipeProtocol.PipeName(Guid.NewGuid());
        using var binding = SessionBinding.Create();
        var nonce = binding.ExportForTrustedLauncher();
        var (pipeVariable, nonceVariable) = purpose == CadSessionPurpose.ReadOnly
            ? (ReadPipeVariable, ReadNonceVariable)
            : (WritePipeVariable, WriteNonceVariable);
        var start = new ProcessStartInfo(_options.ExecutablePath)
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add("/nologo");
        start.ArgumentList.Add("/b");
        start.ArgumentList.Add(bootstrap.Value!.ScriptPath);
        start.Environment[pipeVariable] = pipeName;
        start.Environment[nonceVariable] = nonce;

        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("CAD_PROCESS_START_FAILED");
            Observe("process-started", CadOperationsFor(purpose), null, null, process);
        }
        catch (Exception) when (process is null)
        {
            return Failure<ICadGatewayLease>("CAD_PROCESS_START_FAILED", ErrorCategory.Environment, "AutoCAD could not be started by the current user.");
        }
        catch (Exception)
        {
            return await FailAfterCloseAsync(process!, Error("CAD_PROCESS_INITIALIZATION_FAILED", ErrorCategory.Environment, "AutoCAD started but its managed session could not be initialized."));
        }
        finally
        {
            nonce = string.Empty;
        }

        var restored = await WaitForTrustRestorationAsync(process, bootstrap.Value!, _options.StartupTimeout, cancellationToken);
        if (!restored.IsSuccess)
        {
            Observe("bootstrap-restore-failed", CadOperationsFor(purpose), null, restored.Error);
            return await FailAfterCloseAsync(process, restored.Error!);
        }
        Observe("bootstrap-restored", CadOperationsFor(purpose), null, null);

        var connection = await ConnectAsync(process, pipeName, cancellationToken);
        if (!connection.IsSuccess)
        {
            Observe("connect-failed", CadOperationsFor(purpose), null, connection.Error);
            return await FailAfterCloseAsync(process, connection.Error!);
        }
        Observe("connected", CadOperationsFor(purpose), null, null);
        var pipe = connection.Value!;
        var handshake = CreateHandshake(binding.ExportForTrustedLauncher());
        var response = await pipe.ExchangeAsync(handshake, _options.ExchangeTimeout, cancellationToken);
        if (!response.IsSuccess)
        {
            Observe("handshake-failed", MessageTypes.CapabilitiesRequest, null, response.Error);
            await pipe.DisposeAsync();
            return await FailAfterCloseAsync(process, response.Error!);
        }
        var capabilities = DecodeCapabilities(response.Value!);
        if (!capabilities.IsSuccess)
        {
            Observe("capabilities-failed", MessageTypes.CapabilitiesRequest, null, capabilities.Error);
            await pipe.DisposeAsync();
            return await FailAfterCloseAsync(process, capabilities.Error!);
        }
        var operations = capabilities.Value!.Operations;
        var purposeMatches = purpose switch
        {
            CadSessionPurpose.ReadOnly => operations.Contains(CadOperations.Inspect, StringComparer.Ordinal) &&
                                          operations.Contains(CadOperations.Extract, StringComparer.Ordinal) &&
                                          !operations.Contains(CadOperations.Write, StringComparer.Ordinal),
            CadSessionPurpose.Write => operations.Contains(CadOperations.Write, StringComparer.Ordinal) &&
                                       !operations.Contains(CadOperations.Inspect, StringComparer.Ordinal) &&
                                       !operations.Contains(CadOperations.Extract, StringComparer.Ordinal),
            _ => false
        };
        if (!purposeMatches)
        {
            await pipe.DisposeAsync();
            return await FailAfterCloseAsync(process, Error("CAD_ADAPTER_PURPOSE_MISMATCH", ErrorCategory.Security, "The loaded AutoCAD adapter does not match the requested session purpose."));
        }

        _observer?.Observe(CadBoundaryObservation.Capabilities(capabilities.Value.HostVersion));
        return Results.Success<ICadGatewayLease>(new Lease(process, pipe, capabilities.Value!, _options, _observer));
    }

    public Task<Result<ICadGatewayLease>> OpenAsync(
        CadSessionPurpose purpose,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        if (exchangeTimeout <= TimeSpan.Zero || exchangeTimeout > TimeSpan.FromSeconds(7200))
            return Task.FromResult(Failure<ICadGatewayLease>("CAD_SESSION_TIMEOUT_POLICY_INVALID", ErrorCategory.Configuration,
                "The operation-specific AutoCAD exchange deadline is outside the configured finite bound."));

        // A factory instance owns no live session state.  A cloned immutable option set is therefore
        // safe and keeps startup, cooperative-close and single-lease behavior unchanged.
        return new AutoCadProcessSessionFactory(_options with { ExchangeTimeout = exchangeTimeout }, _observer)
            .OpenAsync(purpose, cancellationToken);
    }

    private Result<bool> ValidateConfiguration(CadSessionPurpose purpose)
    {
        if (!OperatingSystem.IsWindows())
            return Failure<bool>("CAD_PLATFORM_UNSUPPORTED", ErrorCategory.Environment, "AutoCAD sessions require Windows.");
        var read = CadManualLoadBundlePolicy.Resolve(_options.ReadOnlyBundleDirectory, "DwgTranslator.AutoCAD.ReadOnly", "DwgTranslator.AutoCAD.ReadOnly.dll");
        var write = CadManualLoadBundlePolicy.Resolve(_options.WriteBundleDirectory, "DwgTranslator.AutoCAD.Write", "DwgTranslator.AutoCAD.Write.dll");
        if (string.IsNullOrWhiteSpace(_options.ExecutablePath) || !File.Exists(_options.ExecutablePath) ||
            _options.StartupTimeout <= TimeSpan.Zero || _options.ExchangeTimeout <= TimeSpan.Zero || _options.CooperativeCloseTimeout <= TimeSpan.Zero ||
            !read.IsSuccess || !write.IsSuccess ||
            CadManualLoadBundlePolicy.PathsEqual(read.Value!.BundleDirectory, write.Value!.BundleDirectory) ||
            !BootstrapMatches(_options.ReadOnlyBootstrap, read.Value) || !BootstrapMatches(_options.WriteBootstrap, write.Value) ||
            CadManualLoadBundlePolicy.PathsEqual(_options.ReadOnlyBootstrap!.EvidenceDirectory, _options.WriteBootstrap!.EvidenceDirectory))
            return Failure<bool>("CAD_SESSION_CONFIGURATION_INVALID", ErrorCategory.Configuration, "AutoCAD executable, installed adapter bundle and positive timeouts are required.");
        return Results.Success(true);
    }

    private static bool BootstrapMatches(CadNetloadBootstrapOptions? bootstrap, CadManualLoadBundle bundle) =>
        bootstrap is not null && CadNetloadBootstrapScript.IsValid(bootstrap) &&
        CadManualLoadBundlePolicy.PathsEqual(bootstrap.AssemblyPath, bundle.AssemblyPath);

    private static async Task<Result<bool>> WaitForTrustRestorationAsync(Process process, CadNetloadBootstrapArtifact artifact, TimeSpan timeout, CancellationToken cancellationToken)
        => await WaitForTrustRestorationEvidenceAsync(artifact, timeout, () => process.HasExited,
            File.Exists, File.ReadAllText, Task.Delay, cancellationToken).ConfigureAwait(false);

    internal static async Task<Result<bool>> WaitForTrustRestorationEvidenceAsync(
        CadNetloadBootstrapArtifact artifact,
        TimeSpan timeout,
        Func<bool> processExited,
        Func<string, bool> fileExists,
        Func<string, string> readAllText,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var maximumAttempts = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / 100));
        var markerObserved = false;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processExited())
                return Failure<bool>("CAD_BOOTSTRAP_PROCESS_EXITED", ErrorCategory.Security, "AutoCAD exited before temporary trust restoration was proven.");
            if (fileExists(artifact.RestoredMarkerPath))
            {
                markerObserved = true;
                try
                {
                    if (fileExists(artifact.OriginalTrustedPathsPath) &&
                        readAllText(artifact.RestoredMarkerPath).TrimEnd('\r', '\n') == "RESTORED")
                        return Results.Success(true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Existence is visible before AutoLISP completes write/close. Poll within the deadline.
                }
            }
            if (attempt + 1 < maximumAttempts)
                await delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return markerObserved
            ? Failure<bool>("CAD_BOOTSTRAP_EVIDENCE_INVALID", ErrorCategory.Security, "Temporary trust restoration evidence remained incomplete or invalid.")
            : Failure<bool>("CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT", ErrorCategory.Security, "Temporary AutoCAD trust was not proven restored before the handshake.");
    }


    private async Task<Result<WindowsPipeClientSession>> ConnectAsync(Process process, string pipeName, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        ContractError? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return Failure<WindowsPipeClientSession>("CAD_PROCESS_EXITED", ErrorCategory.Environment, "AutoCAD exited before the local adapter became ready.");
            var connected = await WindowsPipeClientSession.ConnectAsync(pipeName, TimeSpan.FromSeconds(1), cancellationToken);
            if (connected.IsSuccess) return connected;
            last = connected.Error;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        return Results.Failure<WindowsPipeClientSession>(last ?? Error("CAD_SESSION_STARTUP_TIMEOUT", ErrorCategory.Environment, "The AutoCAD adapter did not become ready."));
    }

    private static WireEnvelope CreateHandshake(string nonce)
    {
        var payload = new JsonObject
        {
            ["protocolVersion"] = ContractV1.SchemaVersion,
            ["sessionNonce"] = nonce
        };
        var request = WireEnvelope.Request(
            MessageTypes.CapabilitiesRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "sha256:" + new string('0', 64),
            DateTimeOffset.UtcNow,
            payload);
        return request with { IdempotencyKey = EnvelopeIdempotency.Fingerprint(request).Value! };
    }

    private static Result<CadCapabilities> DecodeCapabilities(WireEnvelope response)
    {
        if (response.Status != OperationStatus.Succeeded || response.MessageType != MessageTypes.CapabilitiesResponse)
            return Failure<CadCapabilities>("CAD_CAPABILITIES_INVALID", ErrorCategory.Contract, "AutoCAD did not return a successful capabilities response.");
        try
        {
            var payload = response.Payload?.Deserialize<CapabilitiesPayload>(Json);
            if (payload is null || payload.ProtocolVersion != ContractV1.SchemaVersion ||
                string.IsNullOrWhiteSpace(payload.HostVersion) || payload.HostVersion.Length > 120 ||
                !payload.HostVersion.StartsWith("AutoCAD 2026", StringComparison.Ordinal) || payload.Operations is null || payload.Operations.Count == 0 ||
                payload.Operations.Distinct(StringComparer.Ordinal).Count() != payload.Operations.Count)
                return Failure<CadCapabilities>("CAD_CAPABILITIES_INVALID", ErrorCategory.Contract, "AutoCAD capabilities are incomplete or incompatible.");
            return Results.Success(new CadCapabilities(payload.ProtocolVersion, payload.HostVersion, payload.Operations));
        }
        catch (JsonException)
        {
            return Failure<CadCapabilities>("CAD_CAPABILITIES_INVALID", ErrorCategory.Contract, "AutoCAD capabilities are not valid JSON.");
        }
    }

    private async Task<Result<ICadGatewayLease>> FailAfterCloseAsync(Process process, ContractError original)
    {
        Result<bool> closed;
        try
        {
            closed = await CloseProcessAsync(process, _options.EffectiveCleanupGraceTimeout, CancellationToken.None);
        }
        catch (Exception)
        {
            closed = Failure<bool>("CAD_PROCESS_CLEANUP_FAILED", ErrorCategory.Environment, "AutoCAD cleanup could not be completed; exact child-process termination approval is required.");
        }
        finally
        {
            process.Dispose();
        }
        if (!closed.IsSuccess) Observe("cleanup-failed", "cad.session.close", null, closed.Error);
        var preserved = closed.IsSuccess || original.DiagnosticId is not null
            ? original
            : original with { DiagnosticId = $"cleanup_{closed.Error!.Code}" };
        return Results.Failure<ICadGatewayLease>(preserved);
    }

    private void Observe(string stage, string operation, string? fingerprint, ContractError? error, Process? process = null) =>
        _observer?.Observe(new(stage, operation, fingerprint, null, null, error?.Code, error?.Category, error?.Retryable, error?.DiagnosticId,
            ProcessId: process?.Id, ProcessExecutablePath: ProcessPath(process), ProcessStartedAtUtc: ProcessStart(process),
            TechnicalStage: error?.TechnicalStage, NativeErrorStatus: error?.NativeErrorStatus));

    private static string? ProcessPath(Process? process)
    {
        try
        {
            var path = process?.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (Exception) { return null; }
    }

    private static DateTimeOffset? ProcessStart(Process? process)
    {
        try { return process is null ? null : new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception) { return null; }
    }

    private static string CadOperationsFor(CadSessionPurpose purpose) => purpose == CadSessionPurpose.ReadOnly ? "cad.session.read-only" : "cad.session.write";

    private static async Task<Result<bool>> CloseProcessAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited) return Results.Success(true);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var runningCad = Process.GetProcessesByName("acad");
        var runningCore = Process.GetProcessesByName("accoreconsole");
        try
        {
            if (runningCore.Length != 0 || runningCad.Length != 1 || runningCad[0].Id != process.Id)
                return Failure<bool>("CAD_PROCESS_OWNERSHIP_LOST", ErrorCategory.Security, "The launched AutoCAD process is no longer the only CAD instance.");
        }
        finally
        {
            foreach (var candidate in runningCad.Concat(runningCore)) candidate.Dispose();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var quit = await RequestComQuitAsync(process.Id, timeoutSource.Token);
        if (!quit.IsSuccess)
            return quit.Error!.Code == "CAD_COM_QUIT_CANCELLED" && !cancellationToken.IsCancellationRequested
                ? Failure<bool>("CAD_PROCESS_EXIT_REQUIRED", ErrorCategory.Environment, "AutoCAD did not acknowledge cooperative shutdown; exact child-process termination approval is required.")
                : quit;
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return Failure<bool>("CAD_PROCESS_EXIT_REQUIRED", ErrorCategory.Environment, "AutoCAD did not close within the cleanup grace; exact child-process termination approval is required.");
        timeoutSource.CancelAfter(remaining);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return Results.Success(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure<bool>("CAD_PROCESS_EXIT_REQUIRED", ErrorCategory.Environment, "AutoCAD did not close within the cleanup grace; exact child-process termination approval is required.");
        }
    }

    private static async Task<Result<bool>> RequestComQuitAsync(int expectedProcessId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Result<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? active = null;
            try
            {
                var classResult = CLSIDFromProgID("AutoCAD.Application.25.1", out var classId);
                if (classResult < 0)
                {
                    completion.TrySetResult(Failure<bool>("CAD_COM_PROGID_UNAVAILABLE", ErrorCategory.Environment, "The AutoCAD 2026 COM registration is unavailable."));
                    return;
                }
                while (!cancellationToken.IsCancellationRequested)
                {
                    var ownership = CadProcessOwnership(expectedProcessId);
                    if (ownership == 0)
                    {
                        completion.TrySetResult(Results.Success(true));
                        return;
                    }
                    if (ownership < 0)
                    {
                        completion.TrySetResult(Failure<bool>("CAD_PROCESS_OWNERSHIP_LOST", ErrorCategory.Security, "The launched AutoCAD process is no longer the only CAD instance."));
                        return;
                    }

                    var activeResult = GetActiveObject(ref classId, IntPtr.Zero, out active);
                    if (activeResult >= 0 && active is not null)
                    {
                        try
                        {
                            var windowValue = active.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, active, null, System.Globalization.CultureInfo.InvariantCulture);
                            var window = new IntPtr(Convert.ToInt64(windowValue, System.Globalization.CultureInfo.InvariantCulture));
                            var windowThreadId = GetWindowThreadProcessId(window, out var activeProcessId);
                            if (window == IntPtr.Zero || windowThreadId == 0 || activeProcessId != expectedProcessId)
                            {
                                completion.TrySetResult(Failure<bool>("CAD_COM_PROCESS_MISMATCH", ErrorCategory.Security, "The active AutoCAD COM object does not belong to the launched process."));
                                return;
                            }
                            active.GetType().InvokeMember("Quit", BindingFlags.InvokeMethod, null, active, null, System.Globalization.CultureInfo.InvariantCulture);
                            completion.TrySetResult(Results.Success(true));
                            return;
                        }
                        catch (Exception exception) when (TryComHResult(exception, out var hResult))
                        {
                            if (!IsTransientComRejection(hResult))
                            {
                                completion.TrySetResult(ComQuitFailure(hResult));
                                return;
                            }
                            if (Marshal.IsComObject(active)) Marshal.FinalReleaseComObject(active);
                            active = null;
                        }
                    }
                    if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250))) break;
                }
                completion.TrySetResult(Failure<bool>("CAD_COM_QUIT_CANCELLED", ErrorCategory.Environment, "The cooperative AutoCAD quit request was cancelled."));
            }
            catch (Exception exception) when (TryComHResult(exception, out var hResult))
            {
                completion.TrySetResult(ComQuitFailure(hResult));
            }
            catch (Exception exception) when (exception is TargetInvocationException or InvalidOperationException or FormatException)
            {
                completion.TrySetResult(ComQuitFailure(exception.HResult));
            }
            finally
            {
                if (active is not null && Marshal.IsComObject(active)) Marshal.FinalReleaseComObject(active);
            }
        })
        {
            IsBackground = true,
            Name = "DwgTranslator.AutoCadComQuit"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure<bool>("CAD_COM_QUIT_CANCELLED", ErrorCategory.Environment, "The cooperative AutoCAD quit request was cancelled.");
        }
    }

    private static bool TryComHResult(Exception exception, out int hResult)
    {
        var com = exception as COMException ?? (exception as TargetInvocationException)?.InnerException as COMException;
        hResult = com?.HResult ?? 0;
        return com is not null;
    }

    private static bool IsTransientComRejection(int hResult) =>
        hResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A);

    private static Result<bool> ComQuitFailure(int hResult) => Results.Failure<bool>(new ContractError(
        "CAD_COM_QUIT_FAILED",
        ErrorCategory.Environment,
        "AutoCAD rejected the cooperative COM quit request.",
        false,
        DiagnosticId: $"HRESULT_0x{hResult:X8}"));

    private static int CadProcessOwnership(int expectedProcessId)
    {
        var runningCad = Process.GetProcessesByName("acad");
        var runningCore = Process.GetProcessesByName("accoreconsole");
        try
        {
            if (runningCad.Length == 0 && runningCore.Length == 0) return 0;
            return runningCore.Length == 0 && runningCad.Length == 1 && runningCad[0].Id == expectedProcessId ? 1 : -1;
        }
        finally
        {
            foreach (var candidate in runningCad.Concat(runningCore)) candidate.Dispose();
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid classId);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid classId, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? activeObject);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    private static ContractError Error(string code, ErrorCategory category, string message) => new(code, category, message, false);
    private static Result<T> Failure<T>(string code, ErrorCategory category, string message) => Results.Failure<T>(Error(code, category, message));

    private sealed record CapabilitiesPayload(string ProtocolVersion, string HostVersion, List<string> Operations);

    private sealed class Lease : ICadGatewayLease, ICadGateway
    {
        private readonly Process _process;
        private readonly AutoCadProcessSessionOptions _options;
        private WindowsPipeClientSession? _pipe;
        private bool _closed;
        private readonly ICadBoundaryObserver? _observer;
        private Guid? _lastJobId;
        private string? _lastRequestId;
        private string? _lastRequestFingerprint;
        private readonly string? _processPath;
        private readonly DateTimeOffset? _processStartedAtUtc;

        public Lease(Process process, WindowsPipeClientSession pipe, CadCapabilities capabilities, AutoCadProcessSessionOptions options, ICadBoundaryObserver? observer)
        {
            _process = process;
            _pipe = pipe;
            Capabilities = capabilities;
            _options = options;
            _observer = observer;
            _processPath = ProcessPath(process);
            _processStartedAtUtc = ProcessStart(process);
        }

        public ICadGateway Gateway => this;
        private CadCapabilities Capabilities { get; }

        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_closed
                ? Results.Failure<CadCapabilities>(Error("CAD_SESSION_CLOSED", ErrorCategory.Environment, "The CAD session is closed."))
                : Results.Success(Capabilities));

        public async Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            var fingerprint = EnvelopeIdempotency.Fingerprint(request).Value;
            _lastJobId = request.JobId;
            _lastRequestId = request.CorrelationId.ToString("D");
            _lastRequestFingerprint = fingerprint;
            _observer?.Observe(new("request", request.MessageType, fingerprint, Capabilities.HostVersion, null, null, null, null, null,
                request.JobId, _lastRequestId, _process.Id, _processPath, _processStartedAtUtc));
            var result = _pipe is null || _closed
                ? Results.Failure<WireEnvelope>(Error("CAD_SESSION_CLOSED", ErrorCategory.Environment, "The CAD session is closed."))
                : await _pipe.ExchangeAsync(request, _options.ExchangeTimeout, cancellationToken);
            var error = result.IsSuccess ? result.Value!.Error : result.Error;
            var response = result.Value;
            var responseHash = response is null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(EnvelopeCodec.Encode(response))).ToLowerInvariant();
            int? extractedCount = response?.Payload?["segments"] is JsonArray segments ? segments.Count : null;
            _observer?.Observe(new("response", request.MessageType, fingerprint, Capabilities.HostVersion, null, error?.Code, error?.Category, error?.Retryable, error?.DiagnosticId,
                request.JobId, _lastRequestId, _process.Id, _processPath, _processStartedAtUtc, responseHash, extractedCount,
                result.IsSuccess ? DateTimeOffset.UtcNow : null,
                TechnicalStage: error?.TechnicalStage, NativeErrorStatus: error?.NativeErrorStatus));
            return result;
        }

        public async Task<Result<bool>> CloseAsync(CancellationToken cancellationToken)
        {
            if (_closed) return Results.Success(true);
            _closed = true;
            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
                _pipe = null;
            }
            var quitRequested = DateTimeOffset.UtcNow;
            _observer?.Observe(new("quit-requested", "cad.session.close", _lastRequestFingerprint, Capabilities.HostVersion, null, null, null, null, null,
                _lastJobId, _lastRequestId, _process.Id, _processPath, _processStartedAtUtc, QuitRequestedAtUtc: quitRequested));
            var closed = await CloseProcessAsync(_process, _options.EffectiveCleanupGraceTimeout, cancellationToken);
            if (closed.IsSuccess)
            {
                var exited = DateTimeOffset.UtcNow;
                int? exitCode = null;
                try { exitCode = _process.ExitCode; } catch (InvalidOperationException) { }
                _observer?.Observe(new("process-exited", "cad.session.close", _lastRequestFingerprint, Capabilities.HostVersion, null, null, null, null, null,
                    _lastJobId, _lastRequestId, _process.Id, _processPath, _processStartedAtUtc, QuitRequestedAtUtc: quitRequested,
                    ProcessExitedAtUtc: exited, ExitCode: exitCode));
                _process.Dispose();
            }
            else
            {
                _observer?.Observe(new("cleanup-required", "cad.session.close", _lastRequestFingerprint, Capabilities.HostVersion, null,
                    closed.Error?.Code, closed.Error?.Category, closed.Error?.Retryable, closed.Error?.DiagnosticId,
                    _lastJobId, _lastRequestId, _process.Id, _processPath, _processStartedAtUtc, QuitRequestedAtUtc: quitRequested,
                    RequiresProcessTerminationApproval: closed.Error?.Code == "CAD_PROCESS_EXIT_REQUIRED"));
            }
            return closed;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed) await CloseAsync(CancellationToken.None);
        }
    }
}
