namespace ConnectionManager.Core.Engine;

/// Runs slow work off the engine loop, one job at a time on a pool thread. A job posted while another runs replaces the
/// one still waiting, if any, instead of queueing behind it: only the newest request matters. Jobs handle their own
/// errors. Thread-safe.
internal sealed class LatestWorker
{
    private readonly object _gate = new();
    private Action? _next;
    private Task _running = Task.CompletedTask;
    private bool _busy;

    public void Post(Action job)
    {
        lock (_gate)
        {
            _next = job;
            if (_busy) return;
            _busy = true;
            _running = Task.Run(Drain);
        }
    }

    /// Completes once no job is running or waiting.
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                if (!_busy) return;
                running = _running;
            }
            await running;
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action job;
            lock (_gate)
            {
                if (_next is null)
                {
                    _busy = false;
                    return;
                }
                job = _next;
                _next = null;
            }
            try
            {
                job();
            }
            catch (Exception) // a job that broke its promise must not stop the ones after it
            {
            }
        }
    }
}
