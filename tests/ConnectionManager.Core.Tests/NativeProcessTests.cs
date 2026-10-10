using System.Diagnostics;
using System.Runtime.InteropServices;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Tests;

public sealed class NativeProcessTests : IDisposable
{
    private readonly TempTree _tree = new();

    [Fact]
    public void A_network_path_is_returned_as_it_is_without_a_lookup()
    {
        // Anyone can run a program from a share; expanding its path as SYSTEM would sign in to the server (192.0.2.1 is
        // reserved for documentation, so a lookup would wait about 21 s).
        var watch = Stopwatch.StartNew();

        Assert.Equal(@"\\192.0.2.1\share\GAME~1.EXE", NativeProcess.ToLongPath(@"\\192.0.2.1\share\GAME~1.EXE"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_local_short_name_is_expanded()
    {
        var exe = _tree.Touch(@"Long Folder Name\app.exe");

        Assert.Equal(exe, NativeProcess.ToLongPath(ShortPath(exe)));
    }

    [Fact]
    public void A_path_through_a_junction_is_not_expanded()
    {
        var exe = _tree.Touch(@"Long Folder Name\app.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.GetDirectoryName(exe)!);
        var throughLink = ShortPath(Path.Combine(link, "app.exe"));

        Assert.Equal(throughLink, NativeProcess.ToLongPath(throughLink));
    }

    [Fact]
    public void This_process_has_a_path()
    {
        var raw = NativeProcess.GetImagePath(Environment.ProcessId);

        Assert.EndsWith(Path.GetFileName(Environment.ProcessPath)!, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NativeProcess.ToLongPath(raw!), NativeProcess.GetPath(Environment.ProcessId));
    }

    // The 8.3 form, or the path unchanged when the volume does not make short names.
    private static string ShortPath(string path)
    {
        var buffer = new char[2048];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        Assert.InRange(length, 1u, (uint)buffer.Length - 1);
        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint size);

    public void Dispose() => _tree.Dispose();
}
