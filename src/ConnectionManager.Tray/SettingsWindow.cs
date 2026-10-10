using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using ConnectionManager.Core.Bridge;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ConnectionManager.Tray;

/// What the settings window needs from the tray. Called on the UI thread.
internal interface ISettingsHost
{
    /// Sends a command over the tray's command connection. Never throws: a failure is a Response with Ok false and a
    /// short message, straight away when the service is not connected.
    Task<Response> ForwardAsync(Command command, JsonElement? args);

    /// Dialogs owned by the window and counted as modal. Null when cancelled.
    string? PickProgram(IWin32Window owner);
    string? PickFolder(IWin32Window owner);

    void OpenLogFolder();
    void OpenInBrowser(string url);
}

/// The settings UI (web/dist, copied to ui\ next to the exe) in WebView2, locked to its virtual host. Modeless, one at a
/// time (TrayContext keeps it). Closing it disposes the WebView2, which ends every msedgewebview2.exe it started.
internal sealed class SettingsWindow : Form
{
    private const string NotBuilt = "Settings UI not built.";
    private const string RuntimeMissing = "Microsoft Edge WebView2 is missing. Install it from Microsoft, then try again.";
    private static readonly string UiFolder = Path.Combine(AppContext.BaseDirectory, "ui");

    private readonly ISettingsHost _host;
    private readonly ILog _log;
    private readonly WebView2 _web;
    private CoreWebView2? _core; // set once the page is ours to talk to
    private string? _tab;
    private bool _starting = true;
    private bool _closeWhenStarted;
    private bool _closeNow;
    private bool _picking;

    public SettingsWindow(ISettingsHost host, ILog log, string? tab)
    {
        _host = host;
        _log = log;
        _tab = tab;
        var light = AppsUseLightTheme();
        var canvas = ColorTranslator.FromHtml(light ? "#f3f5f8" : "#0e1218"); // --color-canvas in web/src/style.css: no flash while loading
        Text = "Connection Manager";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = LogicalToDeviceUnits(new Size(1000, 700));
        MinimumSize = SizeFromClientSize(LogicalToDeviceUnits(new Size(800, 600))); // the page is laid out for 800x600
        BackColor = canvas;
        ForeColor = ColorTranslator.FromHtml(light ? "#111827" : "#e7ecf2");
        _web = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false, DefaultBackgroundColor = canvas }; // a dropped file would navigate to file:
        Controls.Add(_web);
        Load += async (_, _) => await StartAsync();
    }

    /// Brings the window forward, on the given tab when there is one.
    public void ShowTab(string? tab)
    {
        if (_closeWhenStarted)
        {
            _closeWhenStarted = false; // closed while starting, then opened again: keep it
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (tab is null) return;
        _tab = tab; // still starting: the first navigation takes it
        if (_core is null) return;
        try
        {
            _core.Navigate(BridgeRouter.PageUrl(tab)); // same document, new hash: the page switches tab
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _log.Warn($"Could not open the {tab} tab: {ex.Message}");
        }
    }

    /// A status pushed by the service, passed on to the page.
    public void PushStatus(StatusDto status) => Reply(BridgeRouter.Push(status));

    /// The tray is exiting: close without waiting for WebView2 to finish starting.
    public void CloseNow()
    {
        _closeNow = true;
        Close();
    }

    // Load is async void: nothing may escape.
    private async Task StartAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(UiFolder, "index.html")))
            {
                _log.Warn($"The settings UI is missing from {UiFolder}: run pnpm build in web\\, then build the tray");
                ShowMessage(NotBuilt);
                return;
            }
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = false,
                AllowSingleSignOnUsingOSPrimaryAccount = false,
                EnableTrackingPrevention = true,
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder(), options); // null: the Evergreen runtime
            if (IsDisposed) return;
            await _web.EnsureCoreWebView2Async(environment); // even when closing: disposing the started control is what ends its processes
            if (IsDisposed || _closeWhenStarted) return;
            Configure(_web.CoreWebView2);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            _log.Warn($"WebView2 runtime not found: {ex.Message}");
            ShowMessage(RuntimeMissing);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) // otherwise the tray exited mid-start, which is no failure
            {
                _log.Error("The settings window could not start", ex);
                ShowMessage("The settings window could not start. The tray's log has the details.");
            }
        }
        finally
        {
            _starting = false;
            if (_closeWhenStarted && !IsDisposed) Close();
        }
    }

    /// %LOCALAPPDATA%\ConnectionManager\WebView2, from the known folder rather than the environment variable.
    private static string UserDataFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) throw new DirectoryNotFoundException("This user has no local application data folder.");
        return Directory.CreateDirectory(Path.Combine(local, "ConnectionManager", "WebView2")).FullName;
    }

    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+P, Ctrl+F, F12...
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false; // alert, confirm, prompt
        settings.IsReputationCheckingRequired = false;   // SmartScreen: only our own files load, so nothing to check or send
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;

        core.SetVirtualHostNameToFolderMapping(BridgeRouter.Host, UiFolder, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            if (!BridgeRouter.IsOurOrigin(e.Uri)) e.Cancel = true;
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!BridgeRouter.IsOurOrigin(e.Uri)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // never a WebView2 pop-up
            if (e.IsUserInitiated && BridgeRouter.ExternalUrl(e.Uri) is { } url) _host.OpenInBrowser(url);
        };
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true; // mailto:, ms-settings:, steam://...
        core.ProcessFailed += (_, e) => _log.Warn($"A WebView2 process failed: {e.ProcessFailedKind}");
        core.WebMessageReceived += OnWebMessageReceived;
        _core = core;
        core.Navigate(BridgeRouter.PageUrl(_tab));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        BridgeRoute route;
        try
        {
            route = BridgeRouter.Route(e.Source, e.WebMessageAsJson);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return; // the window is closing
        }

        switch (route.Kind)
        {
            case BridgeRouteKind.Forward:
                _ = ForwardAsync(route.Id, route.Request!);
                break;
            case BridgeRouteKind.Reject:
                Reply(BridgeRouter.Error(route.Id, route.Error!));
                break;
            case BridgeRouteKind.PickProgram or BridgeRouteKind.PickFolder or BridgeRouteKind.OpenLogFolder:
                // After this handler returns: a dialog's message loop inside a WebView2 event would stall the WebView2.
                BeginInvoke(() => RunInTray(route));
                break;
        }
    }

    private async Task ForwardAsync(int id, Request request)
    {
        try
        {
            Reply(BridgeRouter.Reply(id, await _host.ForwardAsync(request.Cmd, request.Args)));
        }
        catch (Exception ex)
        {
            _log.Error($"Forwarding {request.Cmd} from the settings page failed", ex); // ForwardAsync doesn't throw; nothing escapes either way
        }
    }

    private void RunInTray(BridgeRoute route)
    {
        if (IsDisposed) return;
        if (route.Kind != BridgeRouteKind.OpenLogFolder && _picking)
        {
            Reply(BridgeRouter.Error(route.Id, "A dialog is already open."));
            return;
        }
        try
        {
            switch (route.Kind)
            {
                case BridgeRouteKind.OpenLogFolder:
                    _host.OpenLogFolder(); // shows its own balloon when it fails
                    Reply(BridgeRouter.Reply(route.Id, new Response(route.Id, true)));
                    break;
                default:
                    _picking = true;
                    var path = route.Kind == BridgeRouteKind.PickProgram ? _host.PickProgram(this) : _host.PickFolder(this);
                    Reply(BridgeRouter.Picked(route.Id, path)); // the page sends it to the service, which checks it
                    break;
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            _log.Error($"{route.Kind} failed", ex);
            Reply(BridgeRouter.Error(route.Id, "The dialog could not open."));
        }
        finally
        {
            if (route.Kind != BridgeRouteKind.OpenLogFolder) _picking = false;
        }
    }

    private void Reply(string json)
    {
        if (IsDisposed || _core is not { } core) return;
        try
        {
            // Last gate: the page that gets the answer must still be ours.
            if (BridgeRouter.IsOurOrigin(core.Source)) core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
            // The window is closing.
        }
    }

    private void ShowMessage(string text)
    {
        if (IsDisposed) return;
        Controls.Remove(_web);
        _web.Dispose();
        Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(24) });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Disposed half-way through starting, WebView2 can leave its browser process behind: hide now, close once started.
        // Never when Windows is shutting down or the tray exits.
        if (_starting && !_closeNow && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _closeWhenStarted = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _core = null;
            _web.Dispose(); // closes the controller: every msedgewebview2.exe of this window exits
        }
        base.Dispose(disposing);
    }

    // The page follows the Windows app theme. Windows' default, with no value set, is light.
    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
