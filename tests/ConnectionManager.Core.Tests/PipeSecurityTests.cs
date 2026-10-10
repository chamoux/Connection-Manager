using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Tests;

public sealed class PipeSecurityTests
{
    private static readonly SecurityIdentifier Me = new(PipeTestServer.Me);

    [Fact]
    public async Task The_client_refuses_a_server_not_owned_by_SYSTEM()
    {
        // The squatting defence: whoever creates the name first owns the pipe, and only SYSTEM can make SYSTEM the owner.
        var calls = 0;
        await using var server = new PipeTestServer((_, request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new Response(request.Id, true));
        });
        await using var client = new PipeClient(server.Name);

        var ex = await Assert.ThrowsAsync<PipeServerVerificationException>(() => client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Contains("not SYSTEM", ex.Message);
        Assert.Contains(PipeTestServer.Me, ex.Message);
        Assert.False(client.IsConnected);
        await Task.Delay(200);
        Assert.Equal(0, calls); // nothing was written before the check
        Assert.DoesNotContain(server.Log.Lines, l => l.Contains("Pipe client", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_server_reads_who_the_client_is_from_its_token()
    {
        ClientIdentity? seen = null;
        await using var server = new PipeTestServer((client, request, _) =>
        {
            seen = client;
            return Task.FromResult(new Response(request.Id, true));
        });
        await using var client = await server.ConnectAsync();

        await client.SendAsync(Command.GetStatus, null, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(Me, seen.User);
        Assert.Equal(Process.GetCurrentProcess().SessionId, seen.SessionId);
        Assert.Contains(seen.IntegrityRid, new[] { 0x2000, 0x3000 }); // Medium, or High when elevated
        Assert.False(seen.IsAppContainer);
        Assert.Equal(Environment.ProcessId, seen.Pid);
        Assert.EndsWith(Path.GetFileName(Environment.ProcessPath)!, seen.ExePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(seen.MayChange); // the test runs in the active console or remote session
        await server.Log.WaitForAsync($"pid {Environment.ProcessId}");
    }

    [Theory]
    [InlineData(1, 0x2000, false, false, null)]
    [InlineData(1, 0x3000, false, false, null)]
    [InlineData(1, 0x4000, false, false, null)]
    [InlineData(0, 0x4000, false, false, "session 0")]
    [InlineData(0, 0x4000, false, true, null)]
    [InlineData(1, 0x1000, false, false, "integrity")]
    [InlineData(1, 0, false, false, "integrity")]
    [InlineData(1, 0x2000, true, false, "AppContainer")]
    public void Clients_outside_a_session_below_Medium_or_sandboxed_are_refused(int session, int rid, bool appContainer, bool allowSession0, string? reason)
    {
        var client = new ClientIdentity(Me, session, rid, appContainer, 42, null, MayChange: true);

        var refusal = client.Refusal(allowSession0);

        if (reason is null) Assert.Null(refusal);
        else Assert.Contains(reason, refusal);
    }

    [Fact]
    public void The_real_pipe_can_only_be_created_with_SYSTEM_as_owner()
    {
        // A normal user (and an admin: see the integration tests) can't assign SYSTEM as owner, so only the service makes this pipe.
        var ex = Assert.Throws<Win32Exception>(() => PipeNative.CreatePipe(Unique(), PipeServerHost.Sddl, first: true, maxInstances: 1));

        Assert.Equal(1307, ex.NativeErrorCode); // ERROR_INVALID_OWNER
    }

    [Fact]
    public void The_interactive_users_grant_cannot_add_pipe_instances()
    {
        // 0x12019b is read/write without FILE_CREATE_PIPE_INSTANCE. GW would include it and let any user add an instance
        // that answers clients. Here the current user holds only that mask, so its second instance is refused.
        var name = Unique();
        using var first = PipeNative.CreatePipe(name, $"O:{PipeTestServer.Me}D:P(A;;0x12019b;;;{PipeTestServer.Me})", first: true, maxInstances: 4);

        var ex = Assert.Throws<Win32Exception>(() => PipeNative.CreatePipe(name, PipeTestServer.Sddl, first: false, maxInstances: 4));

        Assert.Equal(5, ex.NativeErrorCode); // ERROR_ACCESS_DENIED
    }

    [Fact]
    public void Generic_write_does_allow_adding_pipe_instances()
    {
        var name = Unique();
        using var first = PipeNative.CreatePipe(name, $"O:{PipeTestServer.Me}D:P(A;;GRGW;;;{PipeTestServer.Me})", first: true, maxInstances: 4);

        using var second = PipeNative.CreatePipe(name, PipeTestServer.Sddl, first: false, maxInstances: 4);

        Assert.True(second.SafePipeHandle is { IsInvalid: false });
    }

    [Fact]
    public void Only_the_first_instance_may_claim_the_name()
    {
        var name = Unique();
        using var first = PipeNative.CreatePipe(name, PipeTestServer.Sddl, first: true, maxInstances: 4);

        var ex = Assert.Throws<Win32Exception>(() => PipeNative.CreatePipe(name, PipeTestServer.Sddl, first: true, maxInstances: 4));

        Assert.Equal(5, ex.NativeErrorCode); // how the server notices a squatter
    }

    [Fact]
    public void A_service_that_does_not_exist_cannot_vouch_for_a_pipe()
    {
        var ex = Assert.Throws<Win32Exception>(() => PipeNative.ServiceProcessId("ConnectionManager-NoSuchService-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(1060, ex.NativeErrorCode); // ERROR_SERVICE_DOES_NOT_EXIST
    }

    [Fact]
    public void A_running_service_reports_its_process_id()
    {
        // The Event Log service runs on every Windows install, and a normal user may query its status.
        var pid = PipeNative.ServiceProcessId("EventLog");

        Assert.True(pid > 0);
        Assert.Equal(0, Process.GetProcessById(pid).SessionId);
    }

    private static string Unique() => "cm-test-" + Guid.NewGuid().ToString("N");
}
