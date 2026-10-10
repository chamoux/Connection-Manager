using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Paths;
using ConnectionManager.Core.Processes;

namespace ConnectionManager.Core.Engine;

// Games outside Steam. The tray finds a full-screen program that keeps the GPU busy and reports it; the service checks
// the report against what it sees itself before the program counts as a game, and only until the tray releases it or
// the process ends. Like your always list it turns Game Mode on, is never blocked, and may connect during Data Saver,
// but it is never saved.
public sealed partial class ServiceEngine
{
    /// How many games outside Steam count at once; a new one pushes out the oldest.
    public const int MaxTransientGames = 4;

    private const string NotRunning = "That program is not running.";

    private readonly List<RunningProcess> _transientGames = []; // oldest first; engine thread only

    private Response ReportFullscreenGame(ClientIdentity client, Request request)
    {
        if (_parts.ProcessLookup is not { } lookup) return Refuse(request, NotAvailable);
        var args = Framing.ParseArgs<GameArgs>(request);
        if (args.Pid == 0) return ReleaseTransientGame(client, request, args.Path);
        if (!_settings.DetectNonSteamGames) return Refuse(request, "Detecting games outside Steam is turned off.");
        if (args.Pid < 0) return Refuse(request, NotRunning);
        if (!SafePaths.TryLocal(args.Path, out var reported)) return Refuse(request, NotLocal);

        // What the service reads about the process decides; the report only has to agree with it.
        if (Lookup(lookup, args.Pid) is not { } process) return Refuse(request, NotRunning);
        if (!SafePaths.TryLocal(process.Path, out var path)) return Refuse(request, NotLocal);
        if (!SamePath(path, reported)) return Refuse(request, "That process is running a different program.");
        if (process.SessionId != client.SessionId) return Refuse(request, "That program runs in another user's session.");
        if (IsWindowsProgram(path)) return Refuse(request, "Programs that are part of Windows can't be games.");
        if (Contains(_settings.NeverGames, path)) return Refuse(request, "That program is on your not-a-game list.");

        var known = _transientGames.FindIndex(g => SamePath(g.Path, path));
        if (known < 0 && _controller.IsGame(path)) return Ok(request); // a Steam game or one on your list counts already
        if (known < 0 && _controller.IsProtectedPath(path))
            return Refuse(request, $"{NameOf(path)} can't be a game: Steam, voice chat, anti-cheat and Connection Manager never are.");

        if (known >= 0) _transientGames.RemoveAt(known); // reported again: the newest now, with its current pid
        else if (_transientGames.Count >= MaxTransientGames)
        {
            _log.Info($"Game outside Steam no longer counted, to make room: {_transientGames[0].Path}");
            _transientGames.RemoveAt(0);
        }
        _transientGames.Add(new RunningProcess(args.Pid, path));
        _controller.SetTransientGames(TransientPaths());
        if (known < 0) _log.Info(Invariant($"Game outside Steam: {path} (pid {args.Pid}), found by {Who(client)}"));
        Tick();
        return Ok(request);
    }

    // Releasing only compares strings, so the path needs no SafePaths check; ParseArgs has capped its length.
    private Response ReleaseTransientGame(ClientIdentity client, Request request, string path)
    {
        if (_transientGames.RemoveAll(g => SamePath(g.Path, path)) == 0) return Ok(request);
        _controller.SetTransientGames(TransientPaths());
        _log.Info($"Game outside Steam released by {Who(client)}: {SafePaths.Printable(path)}");
        Tick();
        return Ok(request);
    }

    private ProcessFacts? Lookup(Func<int, ProcessFacts?> lookup, int pid)
    {
        try
        {
            return lookup(pid);
        }
        catch (Exception ex) // the real lookup never throws; a process that can't be read counts as not running
        {
            LogOnce("lookup " + ex.GetType().Name, "Could not read a reported process", ex);
            return null;
        }
    }

    // A game outside Steam counts only while its own process runs: a pid Windows hands to another program does not.
    private void PruneTransientGames()
    {
        if (_transientGames.Count == 0) return;
        IReadOnlyList<RunningProcess> running;
        try
        {
            running = _processes.GetRunning();
        }
        catch (Exception) // game detection reports it; keep them until the list can be read
        {
            return;
        }
        var ended = _transientGames.FindAll(g => !running.Any(p => p.Pid == g.Pid && SamePath(p.Path, g.Path)));
        if (ended.Count == 0) return;
        foreach (var game in ended)
        {
            _transientGames.Remove(game);
            _log.Info($"Game outside Steam ended: {game.Path}");
        }
        _controller.SetTransientGames(TransientPaths());
    }

    // Turning detection off stops counting the games it found.
    private void DropTransientGamesIfOff()
    {
        if (_settings.DetectNonSteamGames || _transientGames.Count == 0) return;
        _transientGames.Clear();
        _controller.SetTransientGames([]);
        _log.Info("Detecting games outside Steam turned off: the ones it found no longer count");
    }

    private string[] TransientPaths() => _transientGames.Select(g => g.Path).ToArray();
}
