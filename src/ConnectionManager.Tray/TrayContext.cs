using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Tray;

/// Owns the tray icon, the menu, the hotkey, the settings window and the full-screen game detector. The service does
/// the work: a background loop subscribes to its status, and commands go over a second connection (a subscribed one is
/// push-only); the settings page uses both through this class. UI state lives on the UI thread; the pipes are used off
/// it, and their results are posted back.
internal sealed class TrayContext : ApplicationContext, ITrayCommands, ISettingsHost
{
    private const string NotRunningError = "The Connection Manager service is not running.";
    private const string ReportsTab = "reports";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandIdle = TimeSpan.FromSeconds(45); // the service drops a command connection idle for 60 s

    private readonly ILog _log;
    private readonly string _ownLogFolder;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkey = new();
    private readonly SynchronizationContext _ui = new WindowsFormsSynchronizationContext(); // posts to this thread's message queue
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Queue<TrayBalloon> _balloons = new();
    private readonly System.Windows.Forms.Timer _balloonTimer = new() { Interval = 4000 };
    private readonly HashSet<string> _hotkeyWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly FullscreenDetector _detector;
    private PipeClient? _commands; // used under _commandGate only; swapped out at exit
    private long _commandsUsedAt;
    private StatusDto? _status;    // kept while disconnected, so a reconnect announces only real changes
    private bool _connected;
    private string? _hotkeyText;
    private bool _menuDirty = true;
    private SettingsWindow? _settings;
    private bool _lastBalloonOpensReports;
    private int _openDialogs;
    private bool _exitAfterDialogs;
    private bool _exiting;

    public TrayContext(ILog log, string ownLogFolder)
    {
        _log = log;
        _ownLogFolder = ownLogFolder;
        try
        {
            _tray = new NotifyIcon { Icon = _icons[TrayIconKind.Disconnected], Text = TrayView.NotRunning, Visible = true };
            _tray.DoubleClick += (_, _) => ToggleGameMode();
            _tray.BalloonTipClicked += (_, _) =>
            {
                if (_lastBalloonOpensReports) ShowSettings(ReportsTab);
            };
            _hotkey.Pressed += ToggleGameMode;
            _hotkey.TaskbarCreated += ShowIconAgain;
            _balloonTimer.Tick += (_, _) => ShowNextBalloon();
            var stop = _stop.Token;
            // It logs refusals once itself, and never shows a balloon: the user didn't ask for anything.
            _detector = new FullscreenDetector(game => RequestAsync(Command.ReportFullscreenGame, game, stop, quiet: true), log);
            RebuildIfIdle();
            _ = Task.Run(() => SubscribeLoopAsync(stop));
        }
        catch
        {
            Release(); // no ghost icon left in the tray
            throw;
        }
        _log.Info("Connection Manager tray started");
    }

    /// C:\ProgramData\ConnectionManager\logs, which the installer lets users read. From the known folder, never %ProgramData%.
    private static string ServiceLogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ConnectionManager", "logs");

    // Background: connect, verify, subscribe, and post every status to the UI thread. Retries from 1 s, doubling to 30 s.
    private async Task SubscribeLoopAsync(CancellationToken stop)
    {
        var delay = FirstRetry;
        string? lastError = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var client = new PipeClient();
                await client.ConnectAsync(ConnectTimeout, stop);
                await foreach (var status in client.SubscribeAsync(stop))
                {
                    delay = FirstRetry;
                    lastError = null;
                    Post(() => OnStatus(status));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Not running, refused, or something else holds the pipe name (PipeServerVerificationException): all
                // mean "no service to talk to". Logged when the reason changes, not on every retry.
                var error = ex is PipeServerVerificationException
                    ? $"Refusing the pipe server: {ex.Message}"
                    : $"Service not reachable: {ex.GetType().Name}: {ex.Message}";
                if (error != lastError) _log.Warn(error);
                lastError = error;
            }

            Post(OnDisconnected);
            try
            {
                await Task.Delay(delay, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LastRetry.Ticks));
        }
    }

    private void OnStatus(StatusDto status)
    {
        var previous = _status;
        var wasConnected = _connected;
        _status = status;
        _connected = true;
        if (!wasConnected) _log.Info("Connected to the service");

        foreach (var balloon in TrayView.Balloons(previous, status)) ShowBalloon(balloon);
        UpdateIcon();
        RegisterHotkey(status.Settings.Options.Hotkey);
        _detector.SetEnabled(status.Settings.Options.DetectNonSteamGames);
        if (!wasConnected || previous?.Version != status.Version) RequestRebuild(); // heartbeats repeat the version
        _settings?.PushStatus(status);
    }

    private void OnDisconnected()
    {
        if (!_connected) return;
        _connected = false;
        _detector.SetEnabled(false); // the service forgets the games it was told about when it restarts
        _log.Warn("Lost the connection to the service");
        UpdateIcon();
        RequestRebuild();
    }

    private void UpdateIcon()
    {
        _tray.Icon = _icons[TrayView.Icon(_status, _connected)];
        _tray.Text = TrayView.Tooltip(_status, _connected);
    }

    private void RegisterHotkey(string text)
    {
        if (text == _hotkeyText) return;
        _hotkeyText = text;
        if (_hotkey.Register(text))
        {
            _log.Info($"Hotkey {text} registered");
            return;
        }
        _log.Warn($"Hotkey '{text}' is invalid or used by another app");
        if (_hotkeyWarned.Add(text))
            ShowBalloon(new TrayBalloon("Hotkey not available",
                $"{TrayView.Clean(text)} is not a valid hotkey, or another app uses it. Pick another one in Settings.", IsWarning: true));
    }

    private void ToggleGameMode()
    {
        if (_exiting) return;
        if (!_connected || _status is null) ShowBalloon(TrayView.CommandError(NotRunningError));
        else SetGameMode(ModeLogic.Toggle(_status.GameMode));
    }

    // Menu

    private void RequestRebuild()
    {
        _menuDirty = true;
        _ui.Post(_ => RebuildIfIdle(), null); // once the menu Click handler that asked has returned
    }

    // Never swap the menu while it is open, or while a dialog is: the dialog's caller may be a Click handler of the
    // old menu. Closing the menu or the last dialog asks again.
    private void RebuildIfIdle()
    {
        if (_exiting || !_menuDirty || _openDialogs > 0 || _tray.ContextMenuStrip?.Visible == true) return;
        _menuDirty = false;
        var old = _tray.ContextMenuStrip;
        var menu = _connected && _status is { } status ? TrayMenu.Build(status, this) : TrayMenu.BuildDisconnected(this);
        menu.Closed += (_, _) => _ui.Post(_ => RebuildIfIdle(), null);
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    // Explorer restarted: the new taskbar has no icons until each app adds its own again.
    private void ShowIconAgain()
    {
        if (_exiting) return;
        _tray.Visible = false;
        _tray.Visible = true;
    }

    // Commands

    public void SetGameMode(ModeSetting mode) => Send(Command.SetGameMode, new ModeArgs(mode));

    public void SetDataSaver(ModeSetting mode) => Send(Command.SetDataSaver, new ModeArgs(mode));

    public void SetHotspot(string name, bool isHotspot) => Send(Command.SetHotspot, new HotspotArgs(name, isHotspot));

    public void SetMetered(bool on) => Send(Command.SetMetered, new OnArgs(on));

    public void ToggleCatalog(string id) => Send(Command.ToggleCatalog, new IdArgs(id));

    // The service checks every path it is sent; the tray only picks.
    public void AddProgramBlock()
    {
        if (PickProgram("Block this program during games") is { } path) Send(Command.AddBlock, new PathArgs(path));
    }

    public void AddFolderBlock()
    {
        if (PickFolder("Block every program in this folder during games") is { } path) Send(Command.AddBlock, new PathArgs(path, IsFolder: true));
    }

    public void RemoveBlock(UserBlock block)
    {
        if (Confirm($"Stop blocking {block.Path} during games?")) Send(Command.RemoveBlock, new PathArgs(block.Path, block.IsFolder));
    }

    public void AddGame()
    {
        if (PickProgram("Treat this program as a game") is { } path) Send(Command.AddGame, new PathArgs(path));
    }

    public void MarkNotAGame(string path)
    {
        if (Confirm($"{Path.GetFileName(path)} will no longer turn on Game Mode. Continue?")) Send(Command.MarkNotAGame, new PathArgs(path));
    }

    public void RemoveGame(string path, bool fromAlways)
    {
        if (Confirm($"Remove {Path.GetFileName(path)} from this list?")) Send(Command.RemoveGame, new PathArgs(path, fromAlways));
    }

    public void AllowApp()
    {
        if (PickProgram("Let this program use the internet on your hotspot") is { } path) Send(Command.AllowApp, new PathArgs(path));
    }

    public void RemoveAllowedApp(string path)
    {
        if (Confirm($"Stop allowing {Path.GetFileName(path)} on your hotspot?")) Send(Command.RemoveAllowedApp, new PathArgs(path));
    }

    public void OpenSettings() => ShowSettings(null);

    // One window at a time: opening it again brings it forward, on the given tab.
    private void ShowSettings(string? tab)
    {
        if (_exiting) return;
        if (_settings is { IsDisposed: false } open)
        {
            open.ShowTab(tab);
            return;
        }
        var window = new SettingsWindow(this, _log, tab) { Icon = _icons[TrayIconKind.Both] };
        window.FormClosed += (_, _) =>
        {
            if (_settings == window) _settings = null;
        };
        _settings = window;
        window.Show();
    }

    public void RestoreEverything()
    {
        if (Confirm("Put back the Windows settings Connection Manager changed (the Windows download speed limit and the metered setting on your Wi-Fi)?"))
            Send(Command.RestoreEverything, null);
    }

    public void OpenLogFolder()
    {
        var folder = ServiceLogFolder;
        try
        {
            if (!Directory.Exists(folder))
            {
                folder = _ownLogFolder; // no service installed yet: the tray's own log says why it can't connect
                Directory.CreateDirectory(folder);
            }
            StartExplorer(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Error($"Could not open {folder}", ex);
            ShowBalloon(TrayView.CommandError($"Could not open the log folder: {ex.Message}"));
        }
    }

    // Full path: never pick up an explorer.exe from the current or app folder. No shell.
    private static void StartExplorer(string argument)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    public void Exit()
    {
        if (_openDialogs == 0)
        {
            ExitThread();
            return;
        }
        _exitAfterDialogs = true; // exiting under a dialog would dispose what its caller uses next
        ShowBalloon(new TrayBalloon("Connection Manager", "Close the open window to exit."));
    }

    private void Send(Command command, object? args)
    {
        if (_exiting) return;
        var stop = _stop.Token;
        _ = Task.Run(async () =>
        {
            if (await RequestAsync(command, args, stop) is { Ok: false, Error: { } error }) Post(() => ShowBalloon(TrayView.CommandError(error)));
        });
    }

    // Off the UI thread, one command at a time. The service's reply; Ok false with a short message to show when it
    // refused or could not be reached; null once the tray is exiting. Never throws. Quiet: the caller logs the failures.
    private async Task<Response?> RequestAsync(Command command, object? args, CancellationToken stop, bool quiet = false)
    {
        try
        {
            await _commandGate.WaitAsync(stop);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            PipeClient client;
            try
            {
                client = await CommandClientAsync(stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                if (!quiet) _log.Warn($"Could not send {command}: service not reachable: {ex.GetType().Name}: {ex.Message}");
                return Failed(NotRunningError);
            }

            try
            {
                var response = await client.SendAsync(command, args, stop);
                _commandsUsedAt = Environment.TickCount64;
                if (response.Ok) return response;
                if (!quiet) _log.Info($"The service refused {command}: {response.Error}");
                return string.IsNullOrWhiteSpace(response.Error) ? response with { Error = "The service refused the change." } : response;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                if (!quiet) _log.Error($"Sending {command} failed", ex);
                await DropCommandClientAsync();
                return Failed(ex switch
                {
                    TimeoutException => "The service did not answer in time. Try again.",
                    IOException => "Lost the connection to the service. Try again.",
                    _ => $"Could not send the command: {ex.Message}",
                });
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            return null; // exiting
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static Response Failed(string error) => new(0, false, error);

    // Created on first use and replaced when closed, failed, or idle long enough for the service to drop it mid-send.
    private async Task<PipeClient> CommandClientAsync(CancellationToken stop)
    {
        if (_commands is { IsConnected: true } open && Environment.TickCount64 - _commandsUsedAt < CommandIdle.TotalMilliseconds) return open;
        await DropCommandClientAsync();
        var client = new PipeClient();
        try
        {
            await client.ConnectAsync(ConnectTimeout, stop);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        _commandsUsedAt = Environment.TickCount64;
        _commands = client;
        return client;
    }

    private async Task DropCommandClientAsync()
    {
        if (Interlocked.Exchange(ref _commands, null) is { } old) await old.DisposeAsync();
    }

    // The settings window. Its page's commands take the same path as the menu's; status reaches it from the subscription.

    Task<Response> ISettingsHost.ForwardAsync(Command command, JsonElement? args)
    {
        // No connection: say so now, rather than leave the page waiting on a connect attempt.
        if (_exiting || !_connected) return Task.FromResult(Failed(NotRunningError));
        var stop = _stop.Token;
        return Task.Run(async () => await RequestAsync(command, args, stop) ?? Failed(NotRunningError));
    }

    string? ISettingsHost.PickProgram(IWin32Window owner) => PickProgram("Choose a program", owner);

    string? ISettingsHost.PickFolder(IWin32Window owner) => PickFolder("Choose a folder", owner);

    void ISettingsHost.OpenInBrowser(string url)
    {
        try
        {
            StartExplorer(url); // explorer.exe hands an https URL to the default browser
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Error("Could not open a link from the settings page", ex);
        }
    }

    // Balloons. Windows shows one at a time and a new one replaces the last, so they take turns.

    private void ShowBalloon(TrayBalloon balloon)
    {
        if (_exiting) return;
        if (!_balloonTimer.Enabled) Display(balloon);
        else if (_balloons.Count < 5) _balloons.Enqueue(balloon);
    }

    private void ShowNextBalloon()
    {
        if (!_exiting && _balloons.TryDequeue(out var next)) Display(next);
        else _balloonTimer.Stop();
    }

    private void Display(TrayBalloon balloon)
    {
        _lastBalloonOpensReports = balloon.OpensReports;
        _tray.ShowBalloonTip(5000, balloon.Title, balloon.Text, balloon.IsWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        _balloonTimer.Stop();
        _balloonTimer.Start();
    }

    // Dialogs

    // owner: the settings window when its page asked; null for the menu.
    private string? PickProgram(string title, IWin32Window? owner = null)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = "Programs (*.exe)|*.exe" };
        return ShowDialog(dialog, owner) == DialogResult.OK ? dialog.FileName : null;
    }

    private string? PickFolder(string description, IWin32Window? owner = null)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return ShowDialog(dialog, owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private DialogResult ShowDialog(CommonDialog dialog, IWin32Window? owner) => Modal(() => dialog.ShowDialog(owner));

    private bool Confirm(string question) =>
        Modal(() => MessageBox.Show(question, "Connection Manager", MessageBoxButtons.YesNo, MessageBoxIcon.Question)) == DialogResult.Yes;

    // Counts open dialogs so Exit and menu rebuilds wait for them. A pick made after Exit was clicked is dropped.
    private DialogResult Modal(Func<DialogResult> show)
    {
        if (_exitAfterDialogs || _exiting) return DialogResult.Cancel;
        _openDialogs++;
        try
        {
            var result = show();
            return _exitAfterDialogs ? DialogResult.Cancel : result;
        }
        finally
        {
            if (--_openDialogs == 0)
            {
                if (_exitAfterDialogs) ExitThread();
                else _ui.Post(_ => RebuildIfIdle(), null);
            }
        }
    }

    // From a background thread to the UI thread. Once the tray has exited, the work is dropped.
    private void Post(Action action)
    {
        try
        {
            _ui.Post(_ =>
            {
                if (!_exiting) action();
            }, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidAsynchronousStateException or ObjectDisposedException)
        {
            // The message loop is gone: exiting.
        }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            Release();
            _log.Info("Connection Manager tray stopped; the service keeps running");
        }
        finally
        {
            base.ExitThreadCore();
        }
    }

    // Also runs when start-up fails half-way, so the tray icon may not exist yet.
    private void Release()
    {
        _stop.Cancel(); // ends the subscribe loop and any command in flight
        _detector?.Dispose(); // null when start-up failed before it existed
        _settings?.CloseNow(); // before the icon it uses is disposed; its WebView2 processes end with it
        _settings = null;
        _balloonTimer.Stop();
        if (_tray is not null) _tray.Visible = false;
        _hotkey.Dispose();
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _balloonTimer.Dispose();
        _icons.Dispose();
        if (Interlocked.Exchange(ref _commands, null) is { } commands) _ = commands.DisposeAsync().AsTask();
    }
}
