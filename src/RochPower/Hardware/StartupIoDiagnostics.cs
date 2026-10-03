namespace RochPower.Hardware;

/// <summary>
/// Observes values from existing startup operations only. It never calls a transport or driver.
/// Collection, formatting and teardown errors are deliberately isolated from hardware control.
/// </summary>
internal static class StartupIoDiagnostics
{
    [ThreadStatic] private static Scope? _currentScope;

    internal static Scope? Begin()
    {
        try
        {
            var scope = new Scope(_currentScope);
            _currentScope = scope;
            return scope;
        }
        catch { return null; }
    }

    internal static void ObserveEcRead(ushort address, byte value)
    {
        try { if (_currentScope is { } scope) scope.RecordEc($"EC read {Role(address)}(0x{address:X4})=0x{value:X2}"); }
        catch { }
    }

    internal static void ObserveEcWrite(ushort address, byte value, bool ok)
    {
        try { if (_currentScope is { } scope) scope.RecordEc($"EC write {Role(address)}(0x{address:X4})=0x{value:X2} result={ok}"); }
        catch { }
    }

    internal static void ObserveEcFailure(ushort address, bool write, Exception error)
    {
        try { if (_currentScope is { } scope) scope.RecordEc($"EC {(write ? "write" : "read")} {Role(address)}(0x{address:X4}) threw {error.GetType().Name}: {error.Message}"); }
        catch { }
    }

    internal static void ObserveRegisterRead(byte address, byte register, ushort? value, bool word)
    {
        try
        {
            if (_currentScope is { } scope)
                scope.RecordRegister($"regulator address=0x{address:X2} register=0x{register:X2} {(word ? "word" : "byte")} read=" +
                    (value.HasValue ? "0x" + value.Value.ToString(word ? "X4" : "X2") : "failed"));
        }
        catch { }
    }

    internal static void ObserveIoctlResult(uint code, uint requestedOutBytes, bool ok,
        uint bytesReturned, int? error)
    {
        try { _currentScope?.RecordIoctl(code, requestedOutBytes, ok, bytesReturned, error); }
        catch { }
    }

    internal static void ObserveDriverNotIssued(uint code, uint requestedOutBytes)
    {
        try { if (_currentScope is { } scope) scope.RecordEc($"IOCTL 0x{code:X8} not issued: driver not open; requestedOut={requestedOutBytes}"); }
        catch { }
    }

    internal static void ObserveProbeResult(CorePageVerificationResult result)
    {
        try
        {
            if (_currentScope is not { } scope) return;
            scope.RecordResult($"startup result={result.Succeeded} originalPAGE={result.OriginalPage?.ToString() ?? "not read"} " +
                $"restoredPAGE={result.RestoredPage?.ToString() ?? "not read"} selectionAttempted={result.PageSelectionAttempted} " +
                $"restorationAttempted={result.PageRestorationAttempted}");
            if (result.Snapshot is { } raw)
                scope.RecordResult($"raw core snapshot F0=0x{raw.ModeRegister:X4} 21=0x{raw.TargetMv:X4} 23=0x{raw.SecondaryTargetMv:X4}; " +
                    $"decodedMode={raw.Mode} targetMv={raw.TargetMv}");
            scope.RecordResult("startup status: " + result.Status);
        }
        catch { }
    }

    private static string Role(ushort address) => address switch
    {
        0x0463 => "COMMAND/staged echo",
        0x0465 => "ADDRESS/staged echo",
        0x0466 => "REGISTER/staged echo",
        0x0460 => "DOORBELL (88=transfer,C0=latch,80=idle)",
        0x0470 => "WDATA_LO/staged echo",
        0x0471 => "WDATA_HI/staged echo",
        0x04B0 => "RDATA_LO",
        0x04B1 => "RDATA_HI",
        _ => "raw"
    };

    internal sealed class Scope : IDisposable
    {
        private const int MaxEcEvents = 256, MaxRegisterEvents = 24;
        private const int MaxNativeSamples = 32, MaxNativeFailures = 16, MaxNativeSummaryKeys = 32;
        private const int MaxLineChars = 512;
        private readonly Scope? _previous;
        private readonly List<string> _events = new();
        private readonly List<string> _result = new();
        private readonly Dictionary<IoctlObservation, int> _nativeCounts = new();
        private int _sequence, _ecSeen, _registerSeen, _nativeSamples, _nativeFailures, _nativeSeen, _nativeSummaryDropped;

        internal Scope(Scope? previous) => _previous = previous;

        internal void RecordEc(string value)
        {
            int sequence = ++_sequence;
            if (++_ecSeen <= MaxEcEvents) _events.Add(BoundLine($"#{sequence} {value}"));
        }

        internal void RecordRegister(string value)
        {
            int sequence = ++_sequence;
            if (++_registerSeen <= MaxRegisterEvents) _events.Add(BoundLine($"#{sequence} {value}"));
        }

        internal void RecordResult(string value)
        {
            if (_result.Count < 3) _result.Add(BoundLine(value));
        }

        internal void RecordIoctl(uint code, uint requestedOutBytes, bool ok, uint bytesReturned, int? error)
        {
            int sequence = ++_sequence;
            ++_nativeSeen;
            var key = new IoctlObservation(code, requestedOutBytes, ok, bytesReturned, error);
            if (_nativeCounts.TryGetValue(key, out int count)) _nativeCounts[key] = count + 1;
            else if (_nativeCounts.Count < MaxNativeSummaryKeys) _nativeCounts.Add(key, 1);
            else ++_nativeSummaryDropped;
            bool sample = ok ? ++_nativeSamples <= MaxNativeSamples : ++_nativeFailures <= MaxNativeFailures;
            if (sample) _events.Add(BoundLine($"#{sequence} DeviceIoControl {key}"));
        }

        private static string BoundLine(string value) => value.Length <= MaxLineChars
            ? value : value[..(MaxLineChars - 12)] + " [truncated]";

        internal IReadOnlyList<string> Snapshot()
        {
            try
            {
                var lines = new List<string>(_events);
                foreach (var entry in _nativeCounts) lines.Add($"DeviceIoControl summary count={entry.Value} {entry.Key}");
                lines.Add($"trace bounds: ECseen={_ecSeen} ECcap={MaxEcEvents}; registerSeen={_registerSeen} registerCap={MaxRegisterEvents}; " +
                    $"nativeSeen={_nativeSeen} nativeSampleCap={MaxNativeSamples} nativeFailureCap={MaxNativeFailures} " +
                    $"nativeSummaryKeys={_nativeCounts.Count}/{MaxNativeSummaryKeys} summaryDropped={_nativeSummaryDropped}");
                lines.AddRange(_result);
                return Array.AsReadOnly(lines.ToArray());
            }
            catch { return Array.Empty<string>(); }
        }

        public void Dispose()
        {
            try { if (ReferenceEquals(_currentScope, this)) _currentScope = _previous; }
            catch { }
        }

        private readonly record struct IoctlObservation(uint Code, uint RequestedOutBytes, bool Ok, uint Returned, int? Error)
        {
            public override string ToString() => $"code=0x{Code:X8} BOOL={(Ok ? "TRUE" : "FALSE")} bytesReturned={Returned} " +
                $"requestedOutBytes={RequestedOutBytes} Win32Error={Error?.ToString() ?? "none"}";
        }
    }
}
