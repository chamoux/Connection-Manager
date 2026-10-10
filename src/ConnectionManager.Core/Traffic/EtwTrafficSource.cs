using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace ConnectionManager.Core.Traffic;

/// Something that reports bytes per program to a sink until disposed. TrafficMeter restarts it when it faults.
public interface ITrafficSource : IDisposable
{
    void Start();

    /// What stopped the source; null while it runs.
    Exception? Fault { get; }

    /// The first event that could not be read. Bad events are skipped, never fatal.
    Exception? FirstHandlerError { get; }
}

/// Kernel TCP/UDP send and receive events, per program, from a private real-time ETW session. Adapted from NetWatch's
/// EtwCapture. Needs admin rights. The sink runs on the ETW thread, so it must be quick and must not block.
public sealed class EtwTrafficSource : ITrafficSource
{
    public const string DefaultSessionName = "ConnectionManager-Net";

    private readonly Action<string, long, long> _sink;
    private readonly string _sessionName;
    private readonly ProcessPathResolver _resolver;
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _stopping;
    private int _disposed;
    private Exception? _fault;
    private Exception? _firstHandlerError;
    private long _handlerErrors;

    /// The sink gets (program path, bytes in, bytes out).
    public EtwTrafficSource(Action<string, long, long> sink, string sessionName = DefaultSessionName)
        : this(sink, sessionName, new ProcessPathResolver()) { }

    internal EtwTrafficSource(Action<string, long, long> sink, string sessionName, ProcessPathResolver resolver)
    {
        _sink = sink;
        _sessionName = sessionName;
        _resolver = resolver;
    }

    public Exception? Fault => Volatile.Read(ref _fault);

    public bool Faulted => Fault is not null;

    public Exception? FirstHandlerError => Volatile.Read(ref _firstHandlerError);

    /// Events skipped because their handler threw.
    public long HandlerErrors => Interlocked.Read(ref _handlerErrors);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_session is not null) throw new InvalidOperationException("Already started");
        try
        {
            // Create (the default) replaces a session of the same name left behind by a crash.
            // BufferSizeMB must be set before a provider is enabled; TraceEvent's default is 64 MB.
            _session = new TraceEventSession(_sessionName, TraceEventSessionOptions.Create) { StopOnDispose = true, BufferSizeMB = 8 };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.ImageLoad);
            Hook(_session.Source.Kernel);
            _thread = new Thread(Run) { IsBackground = true, Name = "etw-traffic" };
            _thread.Start();
        }
        catch
        {
            try
            {
                Dispose();
            }
            catch (Exception)
            {
                // Keep the start error: it says why (usually no admin rights).
            }
            throw;
        }
    }

    private void Run()
    {
        try
        {
            _session!.Source.Process();
            if (!_stopping) SetFault(new InvalidOperationException($"The ETW session {_sessionName} stopped unexpectedly"));
        }
        catch (Exception ex) // the thread's boundary: an escaping exception would end the process
        {
            if (!_stopping) SetFault(ex);
        }
    }

    // TraceEvent puts the payload PID of kernel network events into ProcessID (checked by NetWatch's ETW spike).
    private void Hook(KernelTraceEventParser k)
    {
        k.TcpIpSend += Guard((TcpIpSendTraceData d) => Net(d.ProcessID, d.saddr, d.daddr, 0, d.size));
        k.TcpIpRecv += Guard((TcpIpTraceData d) => Net(d.ProcessID, d.saddr, d.daddr, d.size, 0));
        k.TcpIpSendIPV6 += Guard((TcpIpV6SendTraceData d) => Net(d.ProcessID, d.saddr, d.daddr, 0, d.size));
        k.TcpIpRecvIPV6 += Guard((TcpIpV6TraceData d) => Net(d.ProcessID, d.saddr, d.daddr, d.size, 0));
        k.UdpIpSend += Guard((UdpIpTraceData d) => Net(d.ProcessID, d.saddr, d.daddr, 0, d.size));
        k.UdpIpRecv += Guard((UdpIpTraceData d) => Net(d.ProcessID, d.saddr, d.daddr, d.size, 0));
        k.UdpIpSendIPV6 += Guard((UpdIpV6TraceData d) => Net(d.ProcessID, d.saddr, d.daddr, 0, d.size));
        k.UdpIpRecvIPV6 += Guard((UpdIpV6TraceData d) => Net(d.ProcessID, d.saddr, d.daddr, d.size, 0));

        k.ProcessStart += Guard((ProcessTraceData d) => _resolver.OnStarted(d.ProcessID, d.ImageFileName));
        k.ProcessDCStart += Guard((ProcessTraceData d) => _resolver.OnStarted(d.ProcessID, d.ImageFileName));
        k.ProcessStop += Guard((ProcessTraceData d) => _resolver.OnStopped(d.ProcessID));

        // The start event has only the file name, and a short-lived program is gone before Windows can be asked for
        // its folder. The image-load event that follows has the full path. DLL loads are dropped at once.
        k.ImageLoad += Guard((ImageLoadTraceData d) => OnImage(d.ProcessID, d.FileName));
        k.ImageDCStart += Guard((ImageLoadTraceData d) => OnImage(d.ProcessID, d.FileName));
    }

    // TraceEvent's dispatch has no catch: an exception escaping a handler unwinds through native ProcessTrace and kills
    // the process. Guard wraps the whole handler, field reads included, so a bad event is counted and skipped.
    private Action<T> Guard<T>(Action<T> handler) => d =>
    {
        try
        {
            handler(d);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _handlerErrors);
            Interlocked.CompareExchange(ref _firstHandlerError, ex, null);
        }
    };

    private void Net(int pid, IPAddress local, IPAddress remote, int bytesIn, int bytesOut)
    {
        if (IsLoopback(remote) || IsLoopback(local)) return;
        _sink(_resolver.Resolve(pid), bytesIn, bytesOut);
    }

    private void OnImage(int pid, string fileName)
    {
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) _resolver.OnImage(pid, fileName);
    }

    // Dual-stack sockets report IPv4 peers as ::ffff:a.b.c.d, which IPAddress.IsLoopback does not recognize.
    internal static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    private void SetFault(Exception ex) => Interlocked.CompareExchange(ref _fault, ex, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _session?.Dispose(); // StopOnDispose stops the kernel session, which ends Process()
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
    }
}
