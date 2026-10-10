using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

/// An in-process PipeServerHost on a unique pipe name. Only SYSTEM can own the real pipe, so the test SDDL is the
/// production one with the current user as owner and granted full access (it creates the extra instances).
internal sealed class PipeTestServer : IAsyncDisposable
{
    public static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

    public static readonly string Sddl = PipeServerHost.Sddl
        .Replace("O:SYG:SY", $"O:{Me}")
        .Replace("(A;;GA;;;SY)", $"(A;;GA;;;SY)(A;;GA;;;{Me})");

    private readonly CancellationTokenSource _stop = new();

    public PipeTestServer(Func<ClientIdentity, Request, CancellationToken, Task<Response>>? handler = null,
        Func<PipeServerOptions, PipeServerOptions>? options = null, bool start = true)
    {
        var defaults = new PipeServerOptions
        {
            PipeName = Name,
            Sddl = Sddl,
            FirstFrameTimeout = TimeSpan.FromSeconds(2),
            IdleTimeout = TimeSpan.FromSeconds(2),
            RetryDelay = TimeSpan.FromMilliseconds(100),
        };
        Host = new PipeServerHost(handler ?? Echo, Log, options?.Invoke(defaults) ?? defaults);
        if (start) Start();
    }

    public string Name { get; } = "cm-test-" + Guid.NewGuid().ToString("N");
    public PipeServerHost Host { get; }
    public SafeLog Log { get; } = new();
    public Task Running { get; private set; } = Task.CompletedTask;
    public CancellationToken Stopping => _stop.Token;

    public void Start() => Running = Host.RunAsync(_stop.Token);

    /// Replies Ok with the request's own arguments as data.
    public static Task<Response> Echo(ClientIdentity client, Request request, CancellationToken ct) =>
        Task.FromResult(new Response(request.Id, true, null, request.Args));

    public async Task<PipeClient> ConnectAsync(Func<PipeClient>? make = null)
    {
        var client = make?.Invoke() ?? new PipeClient(Name) { SkipServerVerification = true };
        await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        return client;
    }

    /// A bare pipe connection for sending frames PipeClient never would.
    public async Task<NamedPipeClientStream> ConnectRawAsync()
    {
        var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await Running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }

    public static StatusDto Status(long version, string problem = "")
    {
        var options = new OptionsDto(2, true, "Ctrl+Alt+G", true, false, 200, true, true, true, true);
        var settings = new SettingsDto(options, [], [], [], [], [], [], []);
        return new StatusDto(version, ModeSetting.Auto, false, null, null, [], 0, ModeSetting.Auto, false, "", 0,
            new NetworkDto("ethernet", null, false, false, null, false, false), problem.Length > 0 ? [problem] : [], settings, null);
    }

    public static byte[] Frame(Command command, int id = 1, object? args = null) =>
        Framing.Serialize(new Request(Framing.Version, id, command,
            args is null ? null : JsonSerializer.SerializeToElement(args, IpcJson.Default.GetTypeInfo(args.GetType())!)));

    /// True when the server has closed the connection: a clean end, or a broken pipe.
    public static async Task<bool> IsClosedAsync(Stream pipe, TimeSpan within)
    {
        using var deadline = new CancellationTokenSource(within);
        try
        {
            return await Framing.ReadFrameAsync(pipe, deadline.Token) is null;
        }
        catch (IOException)
        {
            return true;
        }
    }
}

/// ILog that is safe to write from the server's connection tasks.
internal sealed class SafeLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToList();
    public void Info(string message) => _lines.Enqueue("INFO " + message);
    public void Warn(string message) => _lines.Enqueue("WARN " + message);
    public void Error(string message, Exception? ex = null) => _lines.Enqueue("ERROR " + message);

    public async Task WaitForAsync(string fragment, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > until) Assert.Fail($"No log line with '{fragment}'. Log:\n{string.Join("\n", Lines)}");
            await Task.Delay(20);
        }
    }
}
