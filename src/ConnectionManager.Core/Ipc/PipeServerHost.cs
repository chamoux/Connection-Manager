using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Ipc;

/// Test-only settings. The service uses the defaults: the production pipe name, SDDL and timeouts.
internal sealed record PipeServerOptions
{
    public string PipeName { get; init; } = PipeServerHost.PipeName;
    public string Sddl { get; init; } = PipeServerHost.Sddl;
    public bool AllowSession0 { get; init; }
    public int MaxInstances { get; init; } = 8;
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxPerUser { get; init; } = 3;
    public int MaxPerSession { get; init; } = 4;
    public int MaxSubscribers { get; init; } = 4;
    public int KeptForActive { get; init; } = 3;
    public TimeSpan LogClientEvery { get; init; } = TimeSpan.FromMinutes(10);
    public Func<NamedPipeServerStream, ClientIdentity> Identify { get; init; } = ClientIdentity.FromPipe;
    public Func<int, bool> IsSessionActive { get; init; } = ClientIdentity.IsSessionActive;
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// The service's end of \\.\pipe\ConnectionManager.v1, the boundary between the SYSTEM service and every local process.
/// Each connection: read the first frame, identify the client from its token and refuse session 0, below-Medium and
/// AppContainer clients, then answer requests in order until the client goes quiet. Subscribe turns the connection into
/// a push-only status feed, for the active session only. At most MaxInstances - 1 connections are served at once; one
/// more instance always exists, listening or holding the next client, so the pipe name is never released for another
/// process to take. So no one can hold every connection: at most MaxPerUser per user and MaxPerSession per session, at
/// most MaxSubscribers feeds, and the last KeptForActive free connections only for a client in an active session.
public sealed class PipeServerHost
{
    public const string PipeName = "ConnectionManager.v1";

    /// O:SY     owner SYSTEM: clients check it, and nothing short of SYSTEM (or SeRestorePrivilege) can assign it.
    /// D:P      no inherited entries. Network logons denied; SYSTEM and Administrators full control. Interactive users get
    ///          0x12019b: read and write data and attributes, READ_CONTROL, SYNCHRONIZE. Not GW: it includes
    ///          FILE_CREATE_PIPE_INSTANCE, which would let any user add an instance of this pipe and answer clients.
    ///          No AppContainer entry.
    /// S:ML     Medium mandatory label, no-write-up and no-read-up: Low-integrity processes can't open the pipe.
    public const string Sddl = "O:SYG:SYD:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12019b;;;IU)S:(ML;;NWNR;;;ME)";

    private const int MaxLoggedProblems = 256;
    private const int MaxLoggedClients = 64;

    private readonly Func<ClientIdentity, Request, CancellationToken, Task<Response>> _handler;
    private readonly ILog _log;
    private readonly PipeServerOptions _options;
    private readonly SemaphoreSlim _slots; // connections being served
    private readonly ConcurrentDictionary<Subscriber, byte> _subscribers = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _logged = new();
    private readonly Dictionary<string, int> _perUser = new(StringComparer.Ordinal); // admitted connections; under its own lock
    private readonly Dictionary<int, int> _perSession = new();                         // under _perUser's lock
    private readonly Dictionary<string, long> _clientsLogged = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _latest; // the newest status push, serialized once for every subscriber
    private int _instances;  // pipe instances of ours that exist, listening or connected
    private int _subscriberCount;
    private bool _created;

    /// The handler answers every request except Subscribe, which the host handles. It gets the client's identity with
    /// MayChange refreshed for each request, and should return errors as Response(Ok: false) with a short plain message.
    public PipeServerHost(Func<ClientIdentity, Request, CancellationToken, Task<Response>> handler, ILog log)
        : this(handler, log, new PipeServerOptions())
    {
    }

    internal PipeServerHost(Func<ClientIdentity, Request, CancellationToken, Task<Response>> handler, ILog log, PipeServerOptions options)
    {
        _handler = handler;
        _log = log;
        _options = options;
        _slots = new SemaphoreSlim(options.MaxInstances - 1);
    }

    /// Serves until stop is cancelled, then waits for open connections to close. Call once. Throws Win32Exception when
    /// the first instance can't be created (1307 when not running as SYSTEM); a name held by another process is retried.
    public async Task RunAsync(CancellationToken stop)
    {
        NamedPipeServerStream? listening = null;
        try
        {
            while (true)
            {
                listening ??= await CreateListenerAsync(stop);
                try
                {
                    await listening.WaitForConnectionAsync(stop);
                }
                catch (IOException ex) when (!stop.IsCancellationRequested)
                {
                    // The client left before it was accepted. Replace the instance before closing it, to keep the name.
                    LogOnce("accept", $"A pipe client left before it was accepted: {ex.Message}");
                    var broken = listening;
                    listening = TryCreate();
                    Close(broken);
                    continue;
                }

                var connected = listening;
                listening = null;
                try
                {
                    await _slots.WaitAsync(stop); // all busy: this client waits here, later ones wait to connect
                }
                catch
                {
                    Close(connected);
                    throw;
                }
                listening = TryCreate(); // before the hand-off, while the connected instance still holds the name
                Serve(connected, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            if (listening is not null) Close(listening);
            await Task.WhenAll(_connections.Keys);
        }
    }

    /// Sends the status to every subscriber, newest only: a snapshot not yet sent is replaced, never queued.
    /// Meant for one caller at a time (the Engine loop).
    public void Publish(StatusDto status)
    {
        byte[] push;
        try
        {
            push = Framing.Serialize(new Response(0, true, null, JsonSerializer.SerializeToElement(status, IpcJson.Default.StatusDto)));
        }
        catch (JsonException ex) // an enum value with no name, say
        {
            LogOnce("status json", "The status could not be serialized; subscribers keep the previous one", ex);
            return;
        }
        if (push.Length > IpcLimits.MaxServerFrame)
        {
            LogOnce("status too large", Invariant($"The status is too large to send ({push.Length} bytes); subscribers keep the previous one."));
            return;
        }
        Volatile.Write(ref _latest, push);
        foreach (var subscriber in _subscribers.Keys) subscriber.Offer(push, replace: true);
    }

    private async Task<NamedPipeServerStream> CreateListenerAsync(CancellationToken stop)
    {
        var held = false;
        while (true)
        {
            var first = Volatile.Read(ref _instances) == 0; // none of ours left: claim the name afresh
            try
            {
                var pipe = Create(first);
                if (held) _log.Info($"The pipe name {_options.PipeName} is free again; listening.");
                return pipe;
            }
            catch (Win32Exception) when (first && PipeNative.PipeExists(_options.PipeName))
            {
                // Another process created the name, perhaps to pose as the service. Its pipe decides which error we get
                // (5, or 231 with no free instance), so check the name rather than the code. Clients refuse that pipe
                // (its owner isn't SYSTEM). Keep trying, so the service takes the name back when it is free.
                if (!held) _log.Warn(Invariant($"Another process holds the pipe name {_options.PipeName}; retrying every {_options.RetryDelay.TotalSeconds:0.#} s."));
                held = true;
            }
            catch (Win32Exception ex) when (_created)
            {
                LogOnce("create " + ex.NativeErrorCode, $"Could not create a pipe instance: {ex.Message}");
            }
            await Task.Delay(_options.RetryDelay, stop);
        }
    }

    private NamedPipeServerStream Create(bool first)
    {
        var pipe = PipeNative.CreatePipe(_options.PipeName, _options.Sddl, first, _options.MaxInstances);
        Interlocked.Increment(ref _instances);
        _created = true;
        return pipe;
    }

    /// Another instance of ours exists whenever this is called, so the name can't have changed hands.
    private NamedPipeServerStream? TryCreate()
    {
        try
        {
            return Create(first: false);
        }
        catch (Win32Exception ex)
        {
            LogOnce("create " + ex.NativeErrorCode, $"Could not add a pipe instance: {ex.Message}");
            return null;
        }
    }

    private void Close(NamedPipeServerStream pipe)
    {
        pipe.Dispose();
        Interlocked.Decrement(ref _instances);
    }

    private void Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var connection = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(pipe, stop);
            }
            catch (OperationCanceledException)
            {
                // a timeout or shutdown: drop the client
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception)
            {
                LogOnce(ex.GetType().Name, $"Dropped a pipe client: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogOnce(ex.GetType().Name, "A pipe connection failed", ex);
            }
            finally
            {
                Close(pipe);
                _slots.Release();
            }
        });
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(done => _connections.TryRemove(done, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        byte[]? frame;
        using (var hello = Deadline(stop, _options.FirstFrameTimeout)) frame = await Framing.ReadFrameAsync(pipe, hello.Token);
        if (frame is null) return;

        var client = _options.Identify(pipe); // after a read: a server can impersonate only once the client has written
        if ((client.Refusal(_options.AllowSession0) ?? Admit(client)) is { } refusal)
        {
            // Once per user and reason, so a client retrying in a loop can't fill the log.
            LogOnce(Invariant($"refused {client.User} {refusal}"),
                Invariant($"Refused pipe client pid {client.Pid} ({client.ExePath ?? "path unknown"}), user {client.User}, session {client.SessionId}: {refusal}."));
            return;
        }
        try
        {
            if (ShouldLogClient(client))
                _log.Info(Invariant($"Pipe client: pid {client.Pid} ({client.ExePath ?? "path unknown"}), user {client.User}, session {client.SessionId}, integrity 0x{client.IntegrityRid:x}{(client.IsAppContainer ? ", AppContainer" : "")}."));
            await ServeRequestsAsync(pipe, frame, client, stop);
        }
        finally
        {
            Leave(client);
        }
    }

    private async Task ServeRequestsAsync(NamedPipeServerStream pipe, byte[]? frame, ClientIdentity client, CancellationToken stop)
    {
        while (frame is not null)
        {
            // A frame that doesn't parse closes the connection without a reply: there may be no request id to answer, and
            // our own clients never send one. A future protocol version gets a new pipe name instead.
            if (!Framing.TryParseRequest(frame, out var request, out var error))
            {
                LogOnce("request " + error, Invariant($"Dropped pipe client pid {client.Pid}: {error}"));
                return;
            }
            if (request.Cmd == Command.Subscribe)
            {
                await SubscribeAsync(pipe, request, client, stop);
                return;
            }
            client = client with { MayChange = _options.IsSessionActive(client.SessionId) }; // users switch and disconnect
            await WriteAsync(pipe, await AnswerAsync(client, request, stop), stop);

            using var idle = Deadline(stop, _options.IdleTimeout);
            frame = await Framing.ReadFrameAsync(pipe, idle.Token);
        }
    }

    private async Task<byte[]> AnswerAsync(ClientIdentity client, Request request, CancellationToken stop)
    {
        try
        {
            var response = await _handler(client, request, stop).WaitAsync(stop); // a handler that ignores stop can't hold up shutdown
            var reply = Framing.Serialize(response with { Id = request.Id }); // never 0, which marks a status push
            if (reply.Length <= IpcLimits.MaxServerFrame) return reply;
            LogOnce("too large " + request.Cmd, Invariant($"The reply to {request.Cmd} is too large to send ({reply.Length} bytes)."));
            return Framing.Serialize(new Response(request.Id, false, "The reply is too large."));
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            LogOnce("handler " + request.Cmd + ex.GetType().Name, $"{request.Cmd} failed", ex);
            return Framing.Serialize(new Response(request.Id, false, "Internal error.")); // details stay in the log
        }
    }

    // Null when the client may keep its connection, otherwise why not. Counted until Leave.
    private string? Admit(ClientIdentity client)
    {
        var user = client.User.Value;
        lock (_perUser)
        {
            if (_perUser.GetValueOrDefault(user) >= _options.MaxPerUser) return Invariant($"its user already has {_options.MaxPerUser} connections");
            if (_perSession.GetValueOrDefault(client.SessionId) >= _options.MaxPerSession)
                return Invariant($"its session already has {_options.MaxPerSession} connections");
            // This connection holds a slot already: the free ones are those left for everyone else.
            if (!client.MayChange && _slots.CurrentCount < _options.KeptForActive) return "the free connections are kept for the active session";
            _perUser[user] = _perUser.GetValueOrDefault(user) + 1;
            _perSession[client.SessionId] = _perSession.GetValueOrDefault(client.SessionId) + 1;
            return null;
        }
    }

    private void Leave(ClientIdentity client)
    {
        lock (_perUser)
        {
            Decrement(_perUser, client.User.Value);
            Decrement(_perSession, client.SessionId);
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        if (counts.GetValueOrDefault(key) > 1) counts[key]--;
        else counts.Remove(key);
    }

    // One line per user and program every LogClientEvery, for at most MaxLoggedClients of them at a time: a client that
    // reconnects in a loop, or under a new name each time, can't fill the log.
    private bool ShouldLogClient(ClientIdentity client)
    {
        var key = client.User.Value + "|" + client.ExePath;
        var time = _options.Time;
        var now = time.GetTimestamp();
        lock (_clientsLogged)
        {
            if (_clientsLogged.TryGetValue(key, out var at) && time.GetElapsedTime(at, now) < _options.LogClientEvery) return false;
            if (!_clientsLogged.ContainsKey(key) && _clientsLogged.Count >= MaxLoggedClients)
            {
                foreach (var (logged, loggedAt) in _clientsLogged)
                    if (time.GetElapsedTime(loggedAt, now) >= _options.LogClientEvery) _clientsLogged.Remove(logged);
                if (_clientsLogged.Count >= MaxLoggedClients) return false;
            }
            _clientsLogged[key] = now;
            return true;
        }
    }

    // A subscribed connection is push-only: after the acknowledgement it carries only status pushes (Id 0), and any frame
    // the client sends closes it. Clients send commands on a second connection. So each connection has one writer, and
    // replies never interleave with pushes. The status is for the active session's user only: a client in another
    // session is refused, and a feed ends when its session stops being the active one.
    private async Task SubscribeAsync(NamedPipeServerStream pipe, Request request, ClientIdentity client, CancellationToken stop)
    {
        if (!_options.IsSessionActive(client.SessionId))
        {
            await WriteAsync(pipe, Framing.Serialize(new Response(request.Id, false, "Only the active user can see the status.")), stop);
            return;
        }
        if (Interlocked.Increment(ref _subscriberCount) > _options.MaxSubscribers)
        {
            Interlocked.Decrement(ref _subscriberCount);
            LogOnce("subscribers", Invariant($"Refused a status subscription: {_options.MaxSubscribers} are open already."));
            await WriteAsync(pipe, Framing.Serialize(new Response(request.Id, false, "Too many status subscriptions.")), stop);
            return;
        }
        var subscriber = new Subscriber();
        using var life = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _subscribers.TryAdd(subscriber, 0);
        try
        {
            await WriteAsync(pipe, Framing.Serialize(new Response(request.Id, true)), life.Token);
            subscriber.Offer(Volatile.Read(ref _latest), replace: false); // a Publish since registering is newer: keep it

            var hangUp = Framing.ReadFrameAsync(pipe, life.Token);
            _ = hangUp.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (true)
            {
                var signal = subscriber.WaitAsync(_options.Heartbeat, life.Token);
                if (await Task.WhenAny(hangUp, signal) == hangUp)
                {
                    if (await hangUp is not null) LogOnce("push-only", "Closed a subscribed pipe connection: the client sent a request on it.");
                    return; // the client closed its end, or broke the protocol
                }
                if (!_options.IsSessionActive(client.SessionId)) return; // the user switched away or disconnected
                // Signalled: the newest snapshot. Timed out: a heartbeat with the latest one, so a dead peer is noticed.
                if ((await signal ? subscriber.Take() : Volatile.Read(ref _latest)) is { } push) await WriteAsync(pipe, push, life.Token);
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriber, out _);
            Interlocked.Decrement(ref _subscriberCount);
            life.Cancel(); // ends the pending read and wait
        }
    }

    /// A write that can't finish in time (the client stopped reading) throws, which drops the client. Replies and pushes
    /// may be larger than a client frame; what clients send is still read with the client limit.
    private async Task WriteAsync(Stream pipe, byte[] payload, CancellationToken token)
    {
        using var deadline = Deadline(token, _options.WriteTimeout);
        await Framing.WriteFrameAsync(pipe, payload, IpcLimits.MaxServerFrame, deadline.Token);
    }

    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan after)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(after);
        return source;
    }

    // Each distinct problem is logged once, so a misbehaving client can't fill the disk.
    private void LogOnce(string key, string message, Exception? ex = null)
    {
        if (_logged.Count >= MaxLoggedProblems || !_logged.TryAdd(key, 0)) return;
        if (ex is null) _log.Warn(message);
        else _log.Error(message, ex);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// Newest-only: a single pending snapshot that each Publish replaces, never a queue. The signal is released only when
    /// the slot goes from empty to full, and the slot is emptied only after taking the signal, so its count stays 0 or 1.
    private sealed class Subscriber
    {
        private readonly SemaphoreSlim _signal = new(0, 1);
        private byte[]? _pending;

        public void Offer(byte[]? push, bool replace)
        {
            if (push is null) return;
            var previous = replace ? Interlocked.Exchange(ref _pending, push) : Interlocked.CompareExchange(ref _pending, push, null);
            if (previous is null) _signal.Release();
        }

        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) => _signal.WaitAsync(timeout, token);

        public byte[]? Take() => Interlocked.Exchange(ref _pending, null);
    }
}
