namespace Mutate4CSharp;

// Controlled-buffer credit is distinct from complete physical I/O. The actual
// original retains this ledger; making another reader never renews either.
internal sealed class ResolvedGraphResources
{
    internal const long SemanticLimit = 512L * 1024 * 1024;
    internal const long OriginalIoLimit = 8L * 1024 * 1024 * 1024;
    internal const long LoggerIoLimit = 128L * 1024 * 1024;
    internal const long WorkerIoLimit = 640L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly ResolvedGraphBudget _original;
    private readonly GraphChildGrant? _grant;
    private readonly Dictionary<string, Child> _children = new(StringComparer.Ordinal);
    private readonly long _ioLimit;
    private long _semantic, _io;
    private int _frames;

    internal ResolvedGraphResources(ResolvedGraphBudget original, GraphChildGrant? grant = null)
    {
        _original = original; _grant = grant;
        if (grant is not null) RequireGrant(grant, grant.Role);
        _ioLimit = grant?.IoBytes ?? OriginalIoLimit;
    }

    internal static void RequireGrant(GraphChildGrant grant, string role)
    {
        if (role is not ("logger" or "worker") || grant.Role != role ||
            grant.IoBytes != (role == "logger" ? LoggerIoLimit : WorkerIoLimit) ||
            grant.Nonce.Length != 32 || grant.Nonce.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new EvaluationContractException("Exact fixed original child resource grant required.");
    }

    internal void AdmitSemantic(long bytes)
    {
        _original.CheckWork();
        lock (_gate)
        {
            if (bytes < 0 || bytes > SemanticLimit - _semantic) Refuse("Shared original semantic/buffer admission exceeded.");
            _semantic += bytes;
        }
        _original.CheckWork();
    }

    internal void AdmitIo(long bytes)
    {
        _original.CheckWork();
        lock (_gate)
        {
            if (bytes < 0 || bytes > _ioLimit - _io) Refuse("Original complete physical I/O admission exceeded.");
            _io += bytes; // Includes complete rereads and nonrefundable child grants.
        }
        _original.CheckWork();
    }

    internal void AdmitFrame()
    {
        _original.CheckWork();
        lock (_gate)
        {
            if (_frames == ResolvedGraphInputs.MaximumFiles) Refuse("Shared original input frame admission exceeded.");
            ++_frames;
        }
    }

    internal GraphChildGrant ReserveChild(string role)
    {
        _original.CheckWork();
        lock (_gate)
        {
            if (_grant is not null || _children.Count >= ResolvedGraphCapture.MaxContexts + 1)
                Refuse("Original finite child reservation domain exceeded.");
            var bytes = role switch { "logger" => LoggerIoLimit, "worker" => WorkerIoLimit,
                _ => throw new EvaluationContractException("Closed original child role required.") };
            if (bytes > _ioLimit - _io) Refuse("Insufficient original I/O for complete child before birth.");
            var grant = new GraphChildGrant(role, Guid.NewGuid().ToString("N"), bytes);
            _original.CheckWork();
            _io += bytes;
            _children.Add(grant.Nonce, new(grant));
            return grant;
        }
    }

    // Called only after the creating parent's actual normal native return,
    // exact output read and request/source joins. DATA alone cannot call an owner.
    // No unused reservation is ever refunded, including unknown/failed children.
    internal void ObserveChildReturn(GraphChildGrant actualGrant, GraphResourceUsage usage, long completeOutputBytes)
    {
        _original.CheckWork();
        lock (_gate)
        {
            if (!_children.TryGetValue(actualGrant.Nonce, out var child) ||
                !ReferenceEquals(child.Grant, actualGrant) || child.ReturnObserved || usage.Grant != actualGrant ||
                usage.IoCharged <= 0 || usage.IoCharged > actualGrant.IoBytes || completeOutputBytes <= 0 ||
                completeOutputBytes > actualGrant.IoBytes - usage.IoCharged ||
                usage.SemanticCharged is <= 0 or > SemanticLimit || usage.Frames <= 0 || usage.Frames >= ResolvedGraphInputs.MaximumFiles)
                Refuse("Actual child resource return does not join its original retained reservation.");
            child.ReturnObserved = true;
            child.PreDelivery = usage;
            child.OutputBytes = completeOutputBytes;
        }
    }

    internal GraphResourceUsage ObservePreDelivery()
    {
        _original.CheckWork();
        lock (_gate) return new(_grant, _semantic, _io, _frames);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Refuse(string reason)
    {
        _original.BeginFailureCleanup();
        throw new SnapshotLimitException(reason);
    }

    private sealed class Child(GraphChildGrant grant)
    {
        internal readonly GraphChildGrant Grant = grant;
        internal bool ReturnObserved;
        internal GraphResourceUsage? PreDelivery;
        internal long OutputBytes;
    }
}

internal sealed record GraphChildGrant(string Role, string Nonce, long IoBytes);
// Explicitly BEFORE its own output serialization/write/first-close. Creating
// parent's exact output extent plus normal native return completes that join.
internal sealed record GraphResourceUsage(GraphChildGrant? Grant, long SemanticCharged, long IoCharged, int Frames);
