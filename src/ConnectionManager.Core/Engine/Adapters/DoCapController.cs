using ConnectionManager.Core.Caps;

namespace ConnectionManager.Core.Engine;

/// Drives DoCapManager from the tick: the Windows download cap is wanted while Data Saver is on and Windows downloads
/// are allowed. It is applied on the rising edge, restored on the falling edge, and a new cap value is a restore then an
/// apply. A failed change is reported and tried again a minute later, not every tick. When "Restore everything" puts the
/// cap back while it is still wanted, the next tick applies it again: the restore undoes stuck changes, it doesn't turn
/// Data Saver's cap off. Engine thread only.
public sealed class DoCapController : ICapController
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly DoCapManager _caps;
    private int? _current;  // the cap reached, null for none
    private bool _ours;     // _current was written by us (false when a policy already sets both values)
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string? _problem;

    public DoCapController(DoCapManager caps) => _caps = caps;

    public string? Update(TickContext context)
    {
        var s = context.Settings;
        int? wanted = context.SaverActive && s.AllowWindowsDownloads ? s.WindowsDownloadCapKBps : null;
        if (_ours && !_caps.Active) (_current, _ours) = (null, false); // restored by a restore hook
        if (wanted == _current)
        {
            if (_retryAt != DateTimeOffset.MinValue) (_retryAt, _problem) = (DateTimeOffset.MinValue, null); // a failed change is no longer wanted
            return _problem;
        }
        if (context.Now < _retryAt) return _problem;

        _problem = null;
        if (_current is not null)
        {
            var restored = _caps.Restore();
            _problem = restored.Problem;
            if (!restored.Complete) return Failed(context.Now);
            (_current, _ours) = (null, false);
        }
        if (wanted is { } kbps)
        {
            var applied = _caps.Apply(kbps);
            _problem = applied.Problem is null ? _problem : _problem is null ? applied.Problem : $"{_problem} {applied.Problem}";
            if (!applied.Applied && applied.Problem is not null) return Failed(context.Now);
            (_current, _ours) = (kbps, applied.Applied);
        }
        _retryAt = DateTimeOffset.MinValue;
        return _problem;
    }

    private string? Failed(DateTimeOffset now)
    {
        _retryAt = now + RetryAfter;
        return _problem;
    }
}
