namespace ConnectionManager.Setup.Tests;

public sealed class InstallScriptTests
{
    private const string Modules = @"C:\Windows\system32\WindowsPowerShell\v1.0\Modules;C:\Program Files\WindowsPowerShell\Modules";

    [Fact]
    public void Runtime_settings_go_PSModulePath_is_replaced_and_everything_else_stays()
    {
        // Case-insensitive, like ProcessStartInfo.Environment on Windows.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["COR_ENABLE_PROFILING"] = "1",
            ["COR_PROFILER"] = "{00000000-0000-0000-0000-000000000000}",
            ["cor_profiler_path"] = @"C:\Users\me\profiler.dll",
            ["COMPlus_ETWEnabled"] = "0",
            ["COMPLUS_Version"] = "v2.0",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\Users\me\hook.dll",
            ["dotnet_gcHeapHardLimit"] = "0x10000000",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER_PATH"] = @"C:\Users\me\profiler.dll",
            ["PSMODULEPATH"] = @"C:\Users\me\Documents\WindowsPowerShell\Modules",
            ["Path"] = @"C:\Windows\system32;C:\Users\me\bin",
            ["TEMP"] = @"C:\Users\me\AppData\Local\Temp",
            ["CORRECT"] = "not a COR_ name",
            ["DOTNETTOOLS"] = "not a DOTNET_ name",
            ["MY_COR_SETTING"] = "the prefix is not at the start",
        };

        InstallScript.CleanEnvironment(environment, Modules);

        Assert.Equal(
            [
                new("CORRECT", "not a COR_ name"),
                new("DOTNETTOOLS", "not a DOTNET_ name"),
                new("MY_COR_SETTING", "the prefix is not at the start"),
                new("Path", @"C:\Windows\system32;C:\Users\me\bin"),
                new("PSModulePath", Modules),
                new("TEMP", @"C:\Users\me\AppData\Local\Temp"),
            ],
            environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList<KeyValuePair<string, string?>>());
    }

    [Fact]
    public void PSModulePath_is_set_when_there_was_none()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"C:\Windows" };

        InstallScript.CleanEnvironment(environment, Modules);

        Assert.Equal(Modules, environment["PSModulePath"]);
        Assert.Equal(2, environment.Count);
    }
}
