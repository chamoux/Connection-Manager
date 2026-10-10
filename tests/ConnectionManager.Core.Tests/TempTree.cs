using System.Diagnostics;

namespace ConnectionManager.Core.Tests;

/// A temporary folder tree for one test; deleted on Dispose.
internal sealed class TempTree : IDisposable
{
    private readonly List<string> _junctions = [];

    public string Root { get; } = Directory.CreateTempSubdirectory("cm-test-").FullName;

    /// Creates an empty file (and its folders) and returns its full path.
    public string Touch(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    /// Replaces %ROOT% the way environment variables are expanded on a real PC.
    public string Expand(string text) => text.Replace("%ROOT%", Root);

    /// Makes `link` a junction to `target` and returns `link`. mklink /J needs no admin rights, unlike a symbolic link.
    public string Junction(string link, string target)
    {
        RunSystemTool("cmd.exe", "/c", "mklink", "/J", link, target);
        _junctions.Add(link);
        return link;
    }

    /// Runs a program from System32 and checks that it succeeded.
    public static void RunSystemTool(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        // A recursive delete unmounts junctions first, which needs admin rights; deleting the link alone does not.
        foreach (var link in _junctions) Directory.Delete(link);
        Directory.Delete(Root, recursive: true);
    }
}
