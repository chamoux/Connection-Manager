using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public sealed class PipeServerHostTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_request_gets_its_reply()
    {
        await using var server = new PipeTestServer();
        await using var client = await server.ConnectAsync();

        var reply = await client.SendAsync(Command.SetGameMode, new ModeArgs(ModeSetting.On), Ct);

        Assert.True(reply.Ok);
        Assert.Equal(ModeSetting.On, reply.Data!.Value.Deserialize(IpcJson.Default.ModeArgs)!.Mode);
    }

    [Fact]
    public async Task Request_ids_start_at_1_and_increase()
    {
        var ids = new List<int>();
        await using var server = new PipeTestServer((_, request, _) =>
        {
            lock (ids) ids.Add(request.Id);
            return Task.FromResult(new Response(request.Id, true));
        });
        await using var client = await server.ConnectAsync();

        for (var i = 0; i < 3; i++) Assert.True((await client.SendAsync(Command.GetStatus, null, Ct)).Ok);

        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public async Task A_refused_command_comes_back_as_an_error_reply()
    {
        await using var server = new PipeTestServer((_, request, _) => Task.FromResult(new Response(request.Id, false, "Not now.")));
        await using var client = await server.ConnectAsync();

        var reply = await client.SendAsync(Command.RestoreEverything, null, Ct);

        Assert.False(reply.Ok);
        Assert.Equal("Not now.", reply.Error);
    }

    [Fact]
    public async Task A_handler_exception_is_an_internal_error_and_the_connection_survives()
    {
        await using var server = new PipeTestServer((_, request, _) => request.Cmd == Command.GetUsage
            ? throw new InvalidOperationException("secret detail")
            : Task.FromResult(new Response(request.Id, true)));
        await using var client = await server.ConnectAsync();

        var failed = await client.SendAsync(Command.GetUsage, null, Ct);
        var next = await client.SendAsync(Command.GetStatus, null, Ct);

        Assert.False(failed.Ok);
        Assert.Equal("Internal error.", failed.Error); // no exception text goes to the client
        Assert.True(next.Ok);
        await server.Log.WaitForAsync("ERROR");
    }

    [Fact]
    public async Task The_reply_always_carries_the_request_id()
    {
        // Id 0 would look like a status push.
        await using var server = new PipeTestServer((_, _, _) => Task.FromResult(new Response(0, true)));
        await using var client = await server.ConnectAsync();

        var reply = await client.SendAsync(Command.GetStatus, null, Ct).WaitAsync(Wait);

        Assert.Equal(1, reply.Id);
    }

    [Fact]
    public async Task A_reply_larger_than_a_client_frame_arrives()
    {
        // A status with long settings lists is bigger than anything a client may send.
        var large = JsonSerializer.SerializeToElement(new IdArgs(new string('x', IpcLimits.MaxFrame * 4)), IpcJson.Default.IdArgs);
        await using var server = new PipeTestServer((_, request, _) => Task.FromResult(new Response(request.Id, true, null, large)));
        await using var client = await server.ConnectAsync();

        var reply = await client.SendAsync(Command.GetUsage, null, Ct);

        Assert.True(reply.Ok);
        Assert.Equal(IpcLimits.MaxFrame * 4, reply.Data!.Value.Deserialize(IpcJson.Default.IdArgs)!.Id.Length);
    }

    [Fact]
    public async Task A_status_larger_than_a_client_frame_reaches_subscribers()
    {
        await using var server = new PipeTestServer();
        server.Host.Publish(PipeTestServer.Status(3, new string('p', IpcLimits.MaxFrame * 2)));
        await using var client = await server.ConnectAsync();
        await using var pushes = client.SubscribeAsync(Ct).GetAsyncEnumerator();

        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(3, pushes.Current.Version);
    }

    [Fact]
    public async Task A_reply_too_large_for_a_frame_becomes_an_error()
    {
        var big = JsonSerializer.SerializeToElement(new IdArgs(new string('x', IpcLimits.MaxServerFrame)), IpcJson.Default.IdArgs);
        await using var server = new PipeTestServer((_, request, _) => Task.FromResult(new Response(request.Id, true, null, big)));
        await using var client = await server.ConnectAsync();

        var reply = await client.SendAsync(Command.GetUsage, null, Ct);

        Assert.False(reply.Ok);
        Assert.Equal("The reply is too large.", reply.Error);
    }

    [Fact]
    public async Task A_subscriber_gets_the_current_status_then_each_publish()
    {
        await using var server = new PipeTestServer();
        server.Host.Publish(PipeTestServer.Status(1));
        await using var client = await server.ConnectAsync();
        await using var pushes = client.SubscribeAsync(Ct).GetAsyncEnumerator();

        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(1, pushes.Current.Version);

        server.Host.Publish(PipeTestServer.Status(2));
        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(2, pushes.Current.Version);
    }

    [Fact]
    public async Task A_slow_subscriber_gets_only_the_newest_status()
    {
        await using var server = new PipeTestServer();
        await using var raw = await server.ConnectRawAsync(); // PipeClient keeps only the newest too, so read the wire directly
        await Framing.WriteFrameAsync(raw, PipeTestServer.Frame(Command.Subscribe), Ct);
        Assert.True(Parse(await Framing.ReadFrameAsync(raw, Ct)).Ok);

        // Each push nearly fills a frame, so the pipe buffer is full after one or two and the server's write waits.
        var problem = new string('p', IpcLimits.MaxFrame - 2000);
        for (var v = 1; v <= 50; v++) server.Host.Publish(PipeTestServer.Status(v, problem));
        await Task.Delay(300);

        var versions = new List<long>();
        while (versions.LastOrDefault() != 50)
        {
            using var deadline = new CancellationTokenSource(Wait);
            var push = Parse(await Framing.ReadFrameAsync(raw, deadline.Token));
            Assert.Equal(0, push.Id);
            versions.Add(push.Data!.Value.Deserialize(IpcJson.Default.StatusDto)!.Version);
        }

        Assert.True(versions.Count <= 5, $"got {string.Join(",", versions)}");
        Assert.Equal(versions.Order(), versions);
        Assert.Equal(versions.Distinct(), versions);
    }

    [Fact]
    public async Task A_quiet_subscriber_gets_a_heartbeat()
    {
        await using var server = new PipeTestServer(options: o => o with { Heartbeat = TimeSpan.FromMilliseconds(200) });
        server.Host.Publish(PipeTestServer.Status(7));
        await using var client = await server.ConnectAsync();
        await using var pushes = client.SubscribeAsync(Ct).GetAsyncEnumerator();

        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait)); // nothing published: the latest again

        Assert.Equal(7, pushes.Current.Version);
    }

    [Fact]
    public async Task A_subscribed_connection_is_push_only()
    {
        await using var server = new PipeTestServer();
        await using var raw = await server.ConnectRawAsync();
        await Framing.WriteFrameAsync(raw, PipeTestServer.Frame(Command.Subscribe), Ct);
        Assert.True(Parse(await Framing.ReadFrameAsync(raw, Ct)).Ok);

        await Framing.WriteFrameAsync(raw, PipeTestServer.Frame(Command.GetStatus, id: 2), Ct);

        Assert.True(await PipeTestServer.IsClosedAsync(raw, Wait));
    }

    [Fact]
    public async Task A_subscribed_client_refuses_to_send_commands()
    {
        await using var server = new PipeTestServer();
        server.Host.Publish(PipeTestServer.Status(1));
        await using var client = await server.ConnectAsync();
        await using var pushes = client.SubscribeAsync(Ct).GetAsyncEnumerator();
        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Command.GetStatus, null, Ct));
    }

    [Fact]
    public async Task An_oversize_frame_drops_the_connection()
    {
        await using var server = new PipeTestServer();
        await using var raw = await server.ConnectRawAsync();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10_000_000);

        await raw.WriteAsync(header);

        Assert.True(await PipeTestServer.IsClosedAsync(raw, Wait));
        await using var next = await server.ConnectAsync(); // the server carries on
        Assert.True((await next.SendAsync(Command.GetStatus, null, Ct)).Ok);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"v":2,"id":1,"cmd":"getStatus"}""")]
    [InlineData("""{"v":1,"id":0,"cmd":"getStatus"}""")]
    [InlineData("""{"v":1,"id":1,"cmd":3}""")]
    public async Task A_malformed_request_closes_the_connection_without_a_reply(string json)
    {
        var calls = 0;
        await using var server = new PipeTestServer((_, request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new Response(request.Id, true));
        });
        await using var raw = await server.ConnectRawAsync();

        await Framing.WriteFrameAsync(raw, Encoding.UTF8.GetBytes(json), Ct);

        Assert.True(await PipeTestServer.IsClosedAsync(raw, Wait)); // a frame would make this false
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_client_that_sends_nothing_is_dropped()
    {
        await using var server = new PipeTestServer(options: o => o with { FirstFrameTimeout = TimeSpan.FromMilliseconds(300) });
        await using var raw = await server.ConnectRawAsync();

        Assert.True(await PipeTestServer.IsClosedAsync(raw, Wait));
    }

    [Fact]
    public async Task An_idle_client_is_dropped_after_its_last_request()
    {
        await using var server = new PipeTestServer(options: o => o with { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        await using var client = await server.ConnectAsync();
        Assert.True((await client.SendAsync(Command.GetStatus, null, Ct)).Ok);

        var until = DateTime.UtcNow + Wait;
        while (client.IsConnected && DateTime.UtcNow < until) await Task.Delay(20);

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(Command.GetStatus, null, Ct));
    }

    [Fact]
    public async Task Nine_clients_at_once_are_all_served_seven_at_a_time()
    {
        int active = 0, most = 0;
        var gate = new object();
        await using var server = new PipeTestServer(async (_, request, _) =>
        {
            var now = Interlocked.Increment(ref active);
            lock (gate) most = Math.Max(most, now);
            await Task.Delay(300);
            Interlocked.Decrement(ref active);
            return new Response(request.Id, true);
        }, Identified(n => Who(User(n), n % 9 + 1))); // nine users in nine active sessions, so no per-user or per-session cap applies

        var replies = await Task.WhenAll(Enumerable.Range(0, 9).Select(async _ =>
        {
            await using var client = await server.ConnectAsync();
            return await client.SendAsync(Command.GetStatus, null, Ct);
        })).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(replies, r => Assert.True(r.Ok));
        Assert.InRange(most, 2, 7); // 8 instances: at most 7 served, one kept for the next client
        await using var tenth = await server.ConnectAsync();
        Assert.True((await tenth.SendAsync(Command.GetStatus, null, Ct)).Ok);
    }

    [Fact]
    public async Task Cancelling_a_send_leaves_the_connection_usable()
    {
        var release = new TaskCompletionSource();
        await using var server = new PipeTestServer(async (_, request, _) =>
        {
            if (request.Id == 1) await release.Task;
            return new Response(request.Id, true);
        });
        await using var client = await server.ConnectAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Command.GetStatus, null, cancel.Token));
        release.SetResult(); // its late reply is ignored
        var next = await client.SendAsync(Command.GetStatus, null, Ct).WaitAsync(Wait);

        Assert.Equal(2, next.Id);
    }

    [Fact]
    public async Task Disposing_the_client_fails_a_pending_send()
    {
        var never = new TaskCompletionSource<Response>();
        await using var server = new PipeTestServer((_, _, _) => never.Task);
        var client = await server.ConnectAsync();
        var pending = client.SendAsync(Command.GetStatus, null, Ct);

        await client.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Wait));
        never.SetResult(new Response(1, true));
    }

    [Fact]
    public async Task Stopping_the_server_ends_subscriptions_and_returns()
    {
        var server = new PipeTestServer();
        server.Host.Publish(PipeTestServer.Status(1));
        await using var client = await server.ConnectAsync();
        var seen = new List<long>();
        var reading = Task.Run(async () =>
        {
            await foreach (var status in client.SubscribeAsync(Ct)) lock (seen) seen.Add(status.Version);
        });
        var until = DateTime.UtcNow + Wait;
        while (seen.Count == 0 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.NotEmpty(seen);

        await server.DisposeAsync(); // RunAsync returns once every connection is closed

        await reading.WaitAsync(Wait); // the enumeration simply ends
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(4)] // the server's first instance fails with 5
    [InlineData(1)] // no free instance: 231, a different error the squatter chose
    public async Task A_name_held_by_another_process_is_retried_until_it_is_free(int squatterInstances)
    {
        await using var server = new PipeTestServer(start: false);
        var squatter = PipeNative.CreatePipe(server.Name, PipeTestServer.Sddl, first: true, squatterInstances);
        server.Start();

        await server.Log.WaitForAsync("holds the pipe name");
        Assert.False(server.Running.IsCompleted);
        squatter.Dispose();

        await using var client = await server.ConnectAsync();
        Assert.True((await client.SendAsync(Command.GetStatus, null, Ct)).Ok);
    }

    // Who each connection is, without other accounts or sessions: sessions below 10 count as active.
    private static readonly SecurityIdentifier Alice = new("S-1-5-21-1000-2000-3000-1001");

    private static SecurityIdentifier User(int n) => new($"S-1-5-21-1000-2000-3000-{2000 + n}");

    private static ClientIdentity Who(SecurityIdentifier user, int session, string exe = @"C:\Apps\client.exe") =>
        new(user, session, ClientIdentity.MediumRid, false, 4321, exe, MayChange: session < 10);

    /// The n-th connection (from 0) is identified as identify(n).
    private static Func<PipeServerOptions, PipeServerOptions> Identified(Func<int, ClientIdentity> identify, Func<int, bool>? isSessionActive = null)
    {
        var next = -1;
        return o => o with
        {
            Identify = _ => identify(Interlocked.Increment(ref next)),
            IsSessionActive = isSessionActive ?? (session => session < 10),
            IdleTimeout = TimeSpan.FromSeconds(30),
        };
    }

    /// A raw connection whose first request was answered: it now holds a connection slot.
    private static async Task<NamedPipeClientStream> ServedAsync(PipeTestServer server)
    {
        var raw = await server.ConnectRawAsync();
        await Framing.WriteFrameAsync(raw, PipeTestServer.Frame(Command.GetStatus), Ct);
        using var deadline = new CancellationTokenSource(Wait);
        Assert.True(Parse(await Framing.ReadFrameAsync(raw, deadline.Token)).Ok);
        return raw;
    }

    private static async Task<bool> RefusedAsync(PipeTestServer server)
    {
        await using var raw = await server.ConnectRawAsync();
        await Framing.WriteFrameAsync(raw, PipeTestServer.Frame(Command.GetStatus), Ct);
        return await PipeTestServer.IsClosedAsync(raw, Wait);
    }

    [Fact]
    public async Task A_user_holds_at_most_three_connections_at_once()
    {
        await using var server = new PipeTestServer(options: Identified(_ => Who(Alice, 1)));
        var held = new List<NamedPipeClientStream>();
        for (var i = 0; i < 3; i++) held.Add(await ServedAsync(server));

        Assert.True(await RefusedAsync(server));
        Assert.True(await RefusedAsync(server));
        await server.Log.WaitForAsync("its user already has 3 connections");
        Assert.Single(server.Log.Lines, l => l.Contains("Refused pipe client", StringComparison.Ordinal)); // once per user and reason

        held[0].Dispose();
        var until = DateTime.UtcNow + Wait;
        while (await RefusedAsync(server) && DateTime.UtcNow < until) await Task.Delay(50); // the slot frees once the server notices
        Assert.True(DateTime.UtcNow < until);
        foreach (var pipe in held) pipe.Dispose();
    }

    [Fact]
    public async Task A_session_holds_at_most_four_connections_at_once()
    {
        await using var server = new PipeTestServer(options: Identified(n => Who(User(n), 1)));
        var held = new List<NamedPipeClientStream>();
        for (var i = 0; i < 4; i++) held.Add(await ServedAsync(server));

        Assert.True(await RefusedAsync(server));
        await server.Log.WaitForAsync("its session already has 4 connections");
        foreach (var pipe in held) pipe.Dispose();
    }

    [Fact]
    public async Task The_last_free_connections_are_kept_for_the_active_session()
    {
        // Seven slots: clients from inactive sessions may take one only while three others stay free.
        await using var server = new PipeTestServer(options: Identified(n => n < 5 ? Who(User(n), 11 + n) : Who(Alice, 1)));
        var held = new List<NamedPipeClientStream>();
        for (var i = 0; i < 4; i++) held.Add(await ServedAsync(server));

        Assert.True(await RefusedAsync(server));
        held.Add(await ServedAsync(server)); // the active session's user still gets in
        await server.Log.WaitForAsync("kept for the active session");
        foreach (var pipe in held) pipe.Dispose();
    }

    [Fact]
    public async Task At_most_four_status_subscriptions_are_open_at_once()
    {
        await using var server = new PipeTestServer(options: Identified(n => Who(User(n), n + 1)));
        server.Host.Publish(PipeTestServer.Status(1));
        var subscribed = new List<(PipeClient Client, IAsyncEnumerator<StatusDto> Pushes)>();
        for (var i = 0; i < 4; i++)
        {
            var client = await server.ConnectAsync();
            var pushes = client.SubscribeAsync(Ct).GetAsyncEnumerator();
            Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));
            subscribed.Add((client, pushes));
        }

        await using var fifth = await server.ConnectAsync();
        var ex = await Assert.ThrowsAsync<IOException>(async () => await fifth.SubscribeAsync(Ct).GetAsyncEnumerator().MoveNextAsync());

        Assert.Contains("Too many status subscriptions.", ex.Message);
        foreach (var (client, pushes) in subscribed)
        {
            await pushes.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Only_the_active_session_gets_the_status_and_loses_it_on_switching_away()
    {
        var active = true;
        await using var server = new PipeTestServer(options: o => Identified(n => Who(Alice, n == 0 ? 1 : 11), session => session == 1 && Volatile.Read(ref active))(o)
            with { Heartbeat = TimeSpan.FromMilliseconds(100) });
        server.Host.Publish(PipeTestServer.Status(1));
        await using var mine = await server.ConnectAsync();
        await using var other = await server.ConnectAsync();
        await using var pushes = mine.SubscribeAsync(Ct).GetAsyncEnumerator();
        Assert.True(await pushes.MoveNextAsync().AsTask().WaitAsync(Wait));

        var refused = await Assert.ThrowsAsync<IOException>(async () => await other.SubscribeAsync(Ct).GetAsyncEnumerator().MoveNextAsync());
        Volatile.Write(ref active, false); // the user switched away: the next heartbeat ends the feed

        Assert.Contains("Only the active user can see the status.", refused.Message);
        var until = DateTime.UtcNow + Wait;
        while (await pushes.MoveNextAsync().AsTask().WaitAsync(Wait) && DateTime.UtcNow < until) { }
        Assert.False(mine.IsConnected);
    }

    [Fact]
    public async Task Each_user_and_program_is_logged_once_every_ten_minutes()
    {
        var clock = new FakeTimeProvider();
        await using var server = new PipeTestServer(options: o => Identified(n => Who(Alice, 1, n == 2 ? @"C:\Apps\other.exe" : @"C:\Apps\client.exe"))(o)
            with { Time = clock, MaxPerUser = 8, MaxPerSession = 8 }); // closed connections may still count for a moment

        for (var i = 0; i < 3; i++) (await ServedAsync(server)).Dispose();
        clock.Advance(TimeSpan.FromMinutes(10));
        (await ServedAsync(server)).Dispose();

        var lines = server.Log.Lines.Where(l => l.Contains("Pipe client", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, lines.Count); // client.exe, other.exe, then client.exe again ten minutes later
        Assert.Single(lines, l => l.Contains("other.exe", StringComparison.Ordinal));
    }

    private static Response Parse(byte[]? frame) => JsonSerializer.Deserialize(frame!, IpcJson.Default.Response)!;
}
