using System.ComponentModel;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Integration.Tests;

public sealed class PipeServerTests
{
    [ElevatedFact]
    public async Task The_real_pipe_needs_the_service_to_run_as_SYSTEM()
    {
        // Even an admin can't make SYSTEM the owner (that takes SeRestorePrivilege, which is off), and clients only trust a
        // SYSTEM-owned pipe. So the server must run as SYSTEM, and nobody else can create a pipe that passes the check.
        var host = new PipeServerHost((_, request, _) => Task.FromResult(new Response(request.Id, true)), new NullLog(),
            new PipeServerOptions { PipeName = "cm-it-" + Guid.NewGuid().ToString("N") });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<Win32Exception>(() => host.RunAsync(stop.Token));

        Assert.Equal(1307, ex.NativeErrorCode); // ERROR_INVALID_OWNER
    }

    private sealed class NullLog : ILog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
