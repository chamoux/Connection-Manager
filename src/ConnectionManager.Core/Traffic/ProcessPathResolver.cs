using System.Diagnostics.CodeAnalysis;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Traffic;

/// Maps a process id to its program path from kernel process and image events. Adapted from NetWatch's ProcessResolver.
/// A name or path longer than the pipe allows (IpcLimits.MaxPath) is kept as "Unknown": it could never be shown.
/// Used only from the ETW thread, so it is not thread-safe.
internal sealed class ProcessPathResolver
{
    public const string SystemName = "System";
    public const string UnknownName = "Unknown";
    public const int MaxEntries = 16_384;
    public static readonly TimeSpan KeepAfterExit = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(30);

    private readonly Func<int, string?> _nativePath;
    private readonly Func<string, string> _longPath;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, Entry> _byPid = new();
    private DateTimeOffset _prunedAt;

    /// `nativePath` returns long paths already; `longPath` expands 8.3 short names in image-event paths (only local ones:
    /// NativeProcess.ToLongPath never looks up a network path).
    public ProcessPathResolver(Func<int, string?>? nativePath = null, Func<string, string>? longPath = null, TimeProvider? time = null)
    {
        _nativePath = nativePath ?? NativeProcess.GetPath;
        _longPath = longPath ?? NativeProcess.ToLongPath;
        _time = time ?? TimeProvider.System;
        _prunedAt = _time.GetUtcNow();
    }

    public int Count => _byPid.Count;

    /// A new process, or a reused id: forget what was known about the old one.
    public void OnStarted(int pid, string? imageName) => Set(pid, new Entry { ImageName = Capped(NullIfEmpty(imageName)) });

    /// The first .exe image of a process is its program file; later ones are DLL-style loads of other programs.
    public void OnImage(int pid, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_byPid.TryGetValue(pid, out var entry))
        {
            if (entry.FromImage || entry.Path is not null) return;
            entry.Path = Capped(path);
            entry.FromImage = true;
            return;
        }
        Set(pid, new Entry { Path = Capped(path), FromImage = true });
    }

    public void OnStopped(int pid)
    {
        var now = _time.GetUtcNow();
        if (_byPid.TryGetValue(pid, out var entry)) entry.ExitedAt = now;
        if (now - _prunedAt >= PruneEvery) Prune(now);
    }

    /// The program path, "System" for the kernel, the bare file name when the folder is unknown, or "Unknown".
    public string Resolve(int pid)
    {
        if (pid is 0 or 4) return SystemName;

        if (!_byPid.TryGetValue(pid, out var entry))
        {
            // Started before the session and missed by the rundown. Asked once: no OpenProcess on every packet.
            entry = new Entry { Path = Capped(_nativePath(pid)), Expanded = true, LookedUp = true };
            Set(pid, entry);
        }
        else if (entry.Path is null && !entry.LookedUp && entry.ExitedAt is null)
        {
            entry.Path = Capped(_nativePath(pid));
            entry.Expanded = entry.LookedUp = true;
        }

        if (entry.Path is null) return entry.ImageName ?? UnknownName;
        if (!entry.Expanded)
        {
            // Expanded here rather than on the image event: most processes never touch the network.
            if (entry.Path is not UnknownName) entry.Path = Capped(_longPath(entry.Path));
            entry.Expanded = true;
        }
        return entry.Path;
    }

    public void Prune(DateTimeOffset now)
    {
        _prunedAt = now;
        foreach (var (pid, entry) in _byPid)
            if (entry.ExitedAt is { } exited && now - exited > KeepAfterExit) _byPid.Remove(pid);
    }

    // Stop events can be lost when ETW drops events, so the map has a hard cap as well as the timed prune.
    private void Set(int pid, Entry entry)
    {
        if (_byPid.Count >= MaxEntries && !_byPid.ContainsKey(pid))
        {
            Prune(_time.GetUtcNow());
            if (_byPid.Count >= MaxEntries) _byPid.Clear();
        }
        _byPid[pid] = entry;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    [return: NotNullIfNotNull(nameof(s))]
    private static string? Capped(string? s) => s is { Length: > IpcLimits.MaxPath } ? UnknownName : s;

    private sealed class Entry
    {
        public string? Path;
        public string? ImageName;
        public bool FromImage;
        public bool Expanded;
        public bool LookedUp;
        public DateTimeOffset? ExitedAt;
    }
}
