using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;

namespace ConnectionManager.Core.Ipc;

/// The pipe server could not prove it is the Connection Manager service: another process may be holding the name.
public sealed class PipeServerVerificationException(string message, Exception? inner = null) : IOException(message, inner);

/// One connection to the service. Connect, then either send commands (any number, also concurrently) or subscribe.
/// A subscribed connection is push-only, as the server requires: use a second PipeClient for commands.
/// Nothing is written before the server is verified: owner SYSTEM, session 0, and the process the service manager
/// reports for the ConnectionManager service. Used by the tray and IpcProbe.
public sealed class PipeClient : IAsyncDisposable
{
    public const string ServiceName = "ConnectionManager";

    private const int New = 0, Connecting = 1, Open = 2, Closed = 3;
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    private readonly string _pipeName;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Channel<StatusDto> _pushes = Channel.CreateBounded<StatusDto>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private NamedPipeClientStream? _pipe;
    private Task _reader = Task.CompletedTask;
    private int _state = New;
    private int _subscribed;
    private int _nextId;

    public PipeClient()
        : this(PipeServerHost.PipeName)
    {
    }

    internal PipeClient(string pipeName) => _pipeName = pipeName;

    /// Tests only: talk to an in-process server that can't be SYSTEM.
    internal bool SkipServerVerification { get; init; }

    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan SubscribeSilenceTimeout { get; init; } = TimeSpan.FromSeconds(75); // the server sends a heartbeat every 30 s

    /// False before ConnectAsync, and once the connection has closed for any reason (the server drops idle ones).
    public bool IsConnected => Volatile.Read(ref _state) == Open;

    /// Connects and verifies the server. Once per PipeClient. TimeoutException when the service isn't listening;
    /// PipeServerVerificationException when something else holds the pipe name.
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _state, Connecting, New) != New) throw new InvalidOperationException("A PipeClient connects once.");
        // Identification: the server may read who we are but can't act as us. The default would allow impersonation.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, ct);
            if (!SkipServerVerification) VerifyServer(pipe, ServiceName);
        }
        catch
        {
            await pipe.DisposeAsync();
            Interlocked.CompareExchange(ref _state, Closed, Connecting);
            throw;
        }

        _pipe = pipe;
        if (Interlocked.CompareExchange(ref _state, Open, Connecting) != Connecting)
        {
            await pipe.DisposeAsync();
            throw new ObjectDisposedException(nameof(PipeClient));
        }
        _reader = Task.Run(ReadLoopAsync);
    }

    /// Sends a command and waits for its reply. A refusal comes back as a reply with Ok false; a lost connection
    /// throws IOException and a server that doesn't answer in time throws TimeoutException.
    public Task<Response> SendAsync(Command command, object? args, CancellationToken ct)
    {
        if (command == Command.Subscribe) throw new ArgumentException("Use SubscribeAsync to subscribe.", nameof(command));
        if (Volatile.Read(ref _subscribed) != 0) throw new InvalidOperationException("This connection is subscribed; send commands on another PipeClient.");
        return RequestAsync(command, args, ct);
    }

    /// Status pushes, newest only: a slow reader skips snapshots rather than queueing them. The first is the current
    /// status. Ends when the connection closes, including a server silent for longer than its heartbeat. Leaving the
    /// loop closes the connection.
    public async IAsyncEnumerable<StatusDto> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) != 0) throw new InvalidOperationException("This connection is already subscribed.");
        try
        {
            var ack = await RequestAsync(Command.Subscribe, null, ct);
            if (!ack.Ok) throw new IOException($"The service refused the subscription: {ack.Error}");
            while (await _pushes.Reader.WaitToReadAsync(ct))
            {
                while (_pushes.Reader.TryRead(out var status)) yield return status;
            }
        }
        finally
        {
            Close(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close(new ObjectDisposedException(nameof(PipeClient)));
        await _reader; // never faults: the loop catches everything
        _life.Dispose();
    }

    /// The checks a squatter can't pass: only SYSTEM can own a pipe as SYSTEM, a user's processes don't run in session 0,
    /// and the service manager names the one process that is the service. Every failure counts as "not verified".
    internal static void VerifyServer(NamedPipeClientStream pipe, string serviceName)
    {
        uint session, pid;
        int servicePid;
        IdentityReference? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!PipeNative.GetNamedPipeServerSessionId(pipe.SafePipeHandle, out session)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!PipeNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            servicePid = LocalSystem.Equals(owner) ? PipeNative.ServiceProcessId(serviceName) : 0;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new PipeServerVerificationException($"Could not verify the pipe server: {ex.Message}", ex);
        }

        if (!LocalSystem.Equals(owner))
            throw new PipeServerVerificationException($"The pipe is owned by {owner?.Value ?? "no one"}, not SYSTEM, so it is not the Connection Manager service.");
        if (session != 0)
            throw new PipeServerVerificationException($"The pipe server runs in session {session}, not session 0, so it is not the Connection Manager service.");
        if (servicePid == 0 || pid != servicePid)
            throw new PipeServerVerificationException($"The pipe server is process {pid}, not the {serviceName} service ({(servicePid == 0 ? "not running" : $"process {servicePid}")}).");
    }

    private async Task<Response> RequestAsync(Command command, object? args, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || !IsConnected) throw Volatile.Read(ref _state) is New or Connecting
            ? new InvalidOperationException("Call ConnectAsync first.")
            : new IOException("The connection to the service is closed.");

        var id = NextId();
        var payload = Framing.Serialize(new Request(Framing.Version, id, command, ToElement(args)));
        if (payload.Length > IpcLimits.MaxFrame) throw new ArgumentException("The request is too large to send.", nameof(args));

        var reply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            if (!IsConnected) throw new IOException("The connection to the service is closed."); // Close may have run first
            await WriteAsync(pipe, payload, ct);
            return await reply.Task.WaitAsync(RequestTimeout, ct);
        }
        finally
        {
            _pending.TryRemove(id, out _); // a late reply then finds no one waiting, and is dropped
        }
    }

    private async Task WriteAsync(Stream pipe, byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Not cancelled by the caller: half a frame would corrupt the stream. A write that can't finish closes it.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            deadline.CancelAfter(WriteTimeout);
            await Framing.WriteFrameAsync(pipe, payload, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Close(ex);
            throw new IOException("The connection to the service closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (true)
            {
                byte[]? frame;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
                {
                    if (Volatile.Read(ref _subscribed) != 0) wait.CancelAfter(SubscribeSilenceTimeout);
                    frame = await Framing.ReadFrameAsync(_pipe!, IpcLimits.MaxServerFrame, wait.Token); // a verified server
                }
                if (frame is null) break;
                Dispatch(frame);
            }
        }
        catch (Exception ex)
        {
            reason = ex; // closed, silent, broken or malformed: each one ends the connection
        }
        Close(reason);
    }

    private void Dispatch(byte[] frame)
    {
        var response = JsonSerializer.Deserialize(frame, IpcJson.Default.Response) ?? throw new InvalidDataException("The service sent an empty message.");
        if (response.Id != 0)
        {
            if (_pending.TryRemove(response.Id, out var waiter)) waiter.TrySetResult(response);
            return;
        }
        if (Volatile.Read(ref _subscribed) == 0 || response.Data is not { } data) throw new InvalidDataException("The service sent a status push nobody asked for.");
        _pushes.Writer.TryWrite(data.Deserialize(IpcJson.Default.StatusDto) ?? throw new InvalidDataException("The service sent an empty status."));
    }

    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _state, Closed) == Closed) return;
        _life.Cancel();
        _pipe?.Dispose();
        var error = new IOException("The connection to the service closed.", reason);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter)) waiter.TrySetException(error);
        }
        _pushes.Writer.TryComplete();
    }

    private int NextId()
    {
        int id;
        do id = Interlocked.Increment(ref _nextId) & int.MaxValue; // 0 marks status pushes
        while (id == 0);
        return id;
    }

    private static JsonElement? ToElement(object? args)
    {
        if (args is null) return null;
        var info = IpcJson.Default.GetTypeInfo(args.GetType()) ?? throw new ArgumentException($"{args.GetType().Name} is not an IPC argument type.", nameof(args));
        return JsonSerializer.SerializeToElement(args, info);
    }
}
