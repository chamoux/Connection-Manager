namespace ConnectionManager.Core.Blocking;

public enum TargetKind { App, Service }

/// One thing to cut off from the internet: a program file (App, Value = full path) or a Windows service
/// (Service, Value = service name). Label is only for display.
public sealed record BlockTarget(TargetKind Kind, string Value, string Label)
{
    // Paths and service names compare case-insensitively, so the same exe found twice is one target.
    public bool Equals(BlockTarget? other) =>
        other is not null && Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Value));
}
