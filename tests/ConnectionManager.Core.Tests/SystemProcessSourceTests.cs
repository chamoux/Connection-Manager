using ConnectionManager.Core.Processes;

namespace ConnectionManager.Core.Tests;

public class SystemProcessSourceTests
{
    [Fact]
    public void Looks_up_each_process_path_once()
    {
        var lookups = new List<int>();
        var source = new SystemProcessSource(() => [(10, "p10"), (20, "p20")], pid => { lookups.Add(pid); return $@"C:\p{pid}.exe"; });

        source.GetRunning();
        var second = source.GetRunning();

        Assert.Equal(new[] { 10, 20 }, lookups);
        Assert.Equal(new[] { @"C:\p10.exe", @"C:\p20.exe" }, second.Select(p => p.Path));
    }

    [Fact]
    public void A_reused_pid_is_looked_up_again_after_it_disappears()
    {
        var processes = new List<(int, string)> { (10, "a") };
        var name = "a";
        var source = new SystemProcessSource(() => processes.ToArray(), _ => $@"C:\{name}.exe");
        source.GetRunning();
        processes.Clear();
        source.GetRunning();
        processes.Add((10, "a"));
        name = "b";

        Assert.Equal(@"C:\b.exe", Assert.Single(source.GetRunning()).Path);
    }

    [Fact]
    public void A_pid_handed_to_another_program_between_ticks_is_looked_up_again()
    {
        var current = (Pid: 10, Name: "a");
        var source = new SystemProcessSource(() => [current], _ => $@"C:\{current.Name}.exe");
        source.GetRunning();

        current = (10, "b");

        Assert.Equal(@"C:\b.exe", Assert.Single(source.GetRunning()).Path);
    }

    [Fact]
    public void Skips_system_pids_and_unreadable_paths() =>
        Assert.Empty(new SystemProcessSource(() => [(0, "Idle"), (4, "System"), (30, "x")], _ => null).GetRunning());

    [Fact]
    public void Finds_this_test_process() =>
        Assert.Contains(new SystemProcessSource().GetRunning(), p => p.Pid == Environment.ProcessId);
}
