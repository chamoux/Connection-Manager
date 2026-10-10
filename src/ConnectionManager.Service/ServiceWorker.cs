using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using Microsoft.Extensions.Hosting;

namespace ConnectionManager.Service;

/// Runs the engine and serves the pipe until the service stops. A fatal error ends the process with exit code 1 at
/// once, so the service manager restarts it; the WFP session, and with it every filter, goes with the process.
internal sealed class ServiceWorker(ServiceEngine engine, PipeServerHost pipe, ILog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            engine.Start();
            log.Info("Connection Manager service started");
            // RunAsync throws when the pipe can't be created (1307 when not running as SYSTEM). The engine's loop only
            // ends on dispose, so either one finishing first means something is broken.
            var serving = pipe.RunAsync(stoppingToken);
            var first = await Task.WhenAny(serving, engine.Completion);
            await first;
            if (!stoppingToken.IsCancellationRequested)
                throw new InvalidOperationException(first == serving ? "The pipe server stopped." : "The engine stopped.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception ex)
        {
            Program.Fail(log, "The Connection Manager service hit a fatal error", ex);
            Environment.Exit(1); // the hosting lifetimes keep no exit handler, so this ends the process now
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken); // the pipe closes: no new commands
        await engine.DisposeAsync();              // every filter removed, the WFP session closed, journaled changes restored
        log.Info("Connection Manager service stopped");
    }
}
