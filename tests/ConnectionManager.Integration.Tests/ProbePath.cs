namespace ConnectionManager.Integration.Tests;

/// Finds the WfpProbe helper built in the same configuration as the tests.
internal static class ProbePath
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ConnectionManager.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not find ConnectionManager.slnx above the test folder");

        var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var exe = Path.Combine(dir.FullName, "tools", "ConnectionManager.WfpProbe", "bin", configuration, "net10.0-windows", "ConnectionManager.WfpProbe.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException("Build the solution first; the probe is missing", exe);
    }
}
