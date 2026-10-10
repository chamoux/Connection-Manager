using ConnectionManager.Core.Wifi;
using static ConnectionManager.Core.Tests.FakeWlan;

namespace ConnectionManager.Core.Tests;

public sealed class WifiTunerTests
{
    private readonly FakeWlan _wlan = new();
    private readonly ListLog _log = new();

    private WifiTuner NewTuner() => new(_wlan, _log);

    private IEnumerable<string> Warnings => _log.Lines.Where(l => !l.StartsWith("INFO ", StringComparison.Ordinal));

    [Fact]
    public void Activation_registers_then_pauses_scans_and_turns_on_streaming_on_every_connected_interface()
    {
        _wlan.Adapters.AddRange([new WlanInterface(A, true), new WlanInterface(B, true), new WlanInterface(Guid.NewGuid(), false)]);
        var tuner = NewTuner();

        tuner.SetActive(gameModeActive: true, enabled: true);

        Assert.Equal(["open", "register", "interfaces", "scan A off", "streaming A on", "scan B off", "streaming B on"], _wlan.Calls);
        Assert.True(tuner.Active);
        Assert.Null(tuner.Problem);
        Assert.NotNull(_wlan.Callback);
    }

    [Fact]
    public void Deactivation_unregisters_reverts_and_closes()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        var tuner = NewTuner();
        tuner.SetActive(true, true);
        _wlan.Calls.Clear();

        tuner.SetActive(false, true);

        Assert.Equal(["unregister", "scan A on", "streaming A off", "close"], _wlan.Calls);
        Assert.False(tuner.Active);
        Assert.False(_wlan.IsOpen);
    }

    [Fact]
    public void Turning_the_option_off_during_a_game_reverts()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        var tuner = NewTuner();
        tuner.SetActive(true, true);

        tuner.SetActive(true, enabled: false);

        Assert.False(tuner.Active);
        Assert.False(_wlan.IsOpen);
    }

    [Fact]
    public void Repeated_activation_opens_once_and_repeated_deactivation_does_nothing()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        var tuner = NewTuner();

        tuner.SetActive(true, true);
        tuner.SetActive(true, true);
        tuner.SetActive(true, true);

        Assert.Equal(1, _wlan.Opens);
        Assert.Single(_wlan.Calls, "scan A off");

        tuner.SetActive(false, true);
        _wlan.Calls.Clear();
        tuner.SetActive(false, true);

        Assert.Empty(_wlan.Calls);
    }

    [Fact]
    public void Inactive_or_disabled_never_opens()
    {
        var tuner = NewTuner();

        tuner.SetActive(false, true);
        tuner.SetActive(true, false);

        Assert.Empty(_wlan.Calls);
    }

    [Fact]
    public void A_connect_notification_is_queued_and_reapplied_on_the_next_tick()
    {
        var tuner = NewTuner();
        var signals = 0;
        tuner.ReapplyRequested += () => signals++;
        tuner.SetActive(true, true); // no Wi-Fi connected yet
        _wlan.Calls.Clear();

        _wlan.Callback!(A); // on a WLAN thread in real life: only queues

        Assert.Empty(_wlan.Calls);
        Assert.True(tuner.ReapplyPending);
        Assert.Equal(1, signals);

        tuner.SetActive(true, true);

        Assert.Equal(["scan A off", "streaming A on"], _wlan.Calls);
        Assert.False(tuner.ReapplyPending);

        _wlan.Calls.Clear();
        tuner.SetActive(false, true);

        Assert.Equal(["unregister", "scan A on", "streaming A off", "close"], _wlan.Calls);
    }

    [Fact]
    public void OnConnected_reapplies_to_that_interface_only_while_active()
    {
        var tuner = NewTuner();

        tuner.OnConnected(A);
        Assert.Empty(_wlan.Calls);

        tuner.SetActive(true, true);
        _wlan.Calls.Clear();
        tuner.OnConnected(B);

        Assert.Equal(["scan B off", "streaming B on"], _wlan.Calls);
    }

    [Fact]
    public void A_handler_that_throws_never_reaches_the_wlan_thread()
    {
        var tuner = NewTuner();
        tuner.ReapplyRequested += () => throw new InvalidOperationException("boom");
        tuner.SetActive(true, true);

        _wlan.Callback!(A);
        _wlan.Callback!(A);

        Assert.True(tuner.ReapplyPending);
        Assert.Single(_log.Lines, l => l.Contains("boom", StringComparison.Ordinal) || l.StartsWith("ERROR ", StringComparison.Ordinal));
    }

    [Fact]
    public void Queued_connects_are_capped()
    {
        var tuner = NewTuner();
        tuner.SetActive(true, true);

        for (var i = 0; i < WifiTuner.MaxPending * 3; i++) _wlan.Callback!(Guid.NewGuid());
        _wlan.Calls.Clear();
        tuner.SetActive(true, true);

        Assert.Equal(WifiTuner.MaxPending, _wlan.Calls.Count(c => c.StartsWith("scan ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Not_supported_sets_the_problem_and_logs_once_across_sessions()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.ScanCode = WlanNative.ERROR_NOT_SUPPORTED;
        var tuner = NewTuner();

        tuner.SetActive(true, true);
        tuner.OnConnected(A);

        Assert.Equal("This Wi-Fi adapter doesn't support pausing scans", tuner.Problem);
        Assert.True(tuner.Active); // streaming mode still applied, and closing reverts it
        Assert.Single(_log.Lines, l => l.Contains("error 50)", StringComparison.Ordinal));

        tuner.SetActive(false, true);
        Assert.Null(tuner.Problem);
        Assert.DoesNotContain("scan A on", _wlan.Calls); // never paused, so nothing to put back
        Assert.Contains("streaming A off", _wlan.Calls);

        tuner.SetActive(true, true);
        Assert.Equal("This Wi-Fi adapter doesn't support pausing scans", tuner.Problem);
        Assert.Single(_log.Lines, l => l.Contains("error 50)", StringComparison.Ordinal));
    }

    [Fact]
    public void Streaming_mode_not_supported_is_logged_once_without_a_problem()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.StreamingCode = WlanNative.ERROR_NOT_SUPPORTED;
        var tuner = NewTuner();

        tuner.SetActive(true, true);
        tuner.OnConnected(A);

        Assert.Null(tuner.Problem);
        Assert.Single(_log.Lines, l => l.Contains("error 50)", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_state_from_a_disconnecting_interface_is_logged_once_and_carried_on()
    {
        _wlan.Adapters.AddRange([new WlanInterface(A, true), new WlanInterface(B, true)]);
        _wlan.ScanCode = WlanNative.ERROR_INVALID_STATE;
        _wlan.StreamingCode = WlanNative.ERROR_INVALID_STATE;
        var tuner = NewTuner();

        tuner.SetActive(true, true);

        Assert.Contains("scan B off", _wlan.Calls);
        Assert.Null(tuner.Problem);
        Assert.Equal(2, _log.Lines.Count(l => l.Contains("error 5023)", StringComparison.Ordinal))); // once for scans, once for streaming
        Assert.Empty(Warnings);

        _wlan.Calls.Clear();
        tuner.SetActive(false, true);
        Assert.Equal(["unregister", "close"], _wlan.Calls); // nothing took, so nothing to put back
    }

    [Theory]
    [InlineData(WlanNative.ERROR_SERVICE_NOT_ACTIVE)]
    [InlineData(WlanNative.ERROR_MOD_NOT_FOUND)]
    [InlineData(WlanNative.RPC_S_SERVER_UNAVAILABLE)]
    public void No_wlan_service_is_silent_and_tried_once_per_session(uint code)
    {
        _wlan.OpenCode = code;
        var tuner = NewTuner();

        tuner.SetActive(true, true);
        tuner.SetActive(true, true);

        Assert.Equal(["open"], _wlan.Calls);
        Assert.False(tuner.Active);
        Assert.Null(tuner.Problem);
        Assert.Empty(Warnings);

        tuner.SetActive(false, true);
        tuner.SetActive(true, true);
        Assert.Equal(["open", "open"], _wlan.Calls); // the next game tries again
    }

    [Fact]
    public void An_unexpected_open_failure_is_logged_once_without_a_problem()
    {
        _wlan.OpenCode = 5; // ERROR_ACCESS_DENIED
        var tuner = NewTuner();

        tuner.SetActive(true, true);
        tuner.SetActive(false, true);
        tuner.SetActive(true, true);

        Assert.Null(tuner.Problem);
        Assert.Single(Warnings);
    }

    [Fact]
    public void No_interfaces_is_silent_and_keeps_listening_for_a_connect()
    {
        var tuner = NewTuner();

        tuner.SetActive(true, true);

        Assert.Equal(["open", "register", "interfaces"], _wlan.Calls);
        Assert.True(tuner.Active);
        Assert.Null(tuner.Problem);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void A_failed_registration_is_logged_and_the_rest_still_applies()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.RegisterCode = 87;
        var tuner = NewTuner();

        tuner.SetActive(true, true);

        Assert.Contains("scan A off", _wlan.Calls);
        Assert.Single(Warnings);
    }

    [Fact]
    public void Dispose_while_active_reverts_and_closes_once()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        var tuner = NewTuner();
        tuner.SetActive(true, true);
        _wlan.Calls.Clear();

        tuner.Dispose();
        tuner.Dispose();
        tuner.SetActive(true, true);

        Assert.Equal(["unregister", "scan A on", "streaming A off", "close"], _wlan.Calls);
        Assert.False(tuner.Active);
    }

    [Fact]
    public void Dispose_while_inactive_touches_nothing()
    {
        var tuner = NewTuner();

        tuner.Dispose();

        Assert.Empty(_wlan.Calls);
    }
}
