using System.Security.Principal;
using ConnectionManager.Core.Engine;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public sealed class ActiveUserTests
{
    private static readonly UserProfile Alice = new("S-1-5-21-1-2-3-1001", @"C:\Users\alice\AppData\Local", @"C:\Users\alice\AppData\Roaming", @"C:\Users\alice");

    // The service's own environment: these must never stand in for the user's folders.
    private static string? ServiceVariable(string name) => name.ToUpperInvariant() switch
    {
        "LOCALAPPDATA" => @"C:\Windows\system32\config\systemprofile\AppData\Local",
        "PROGRAMFILES(X86)" => @"C:\Program Files (x86)",
        _ => null,
    };

    [Theory]
    [InlineData(@"%LOCALAPPDATA%\Google\Update\GoogleUpdate.exe", @"C:\Users\alice\AppData\Local\Google\Update\GoogleUpdate.exe")]
    [InlineData(@"%AppData%\Tool\tool.exe", @"C:\Users\alice\AppData\Roaming\Tool\tool.exe")]
    [InlineData(@"%userprofile%\bin\x.exe", @"C:\Users\alice\bin\x.exe")]
    [InlineData(@"%ProgramFiles(x86)%\Google\x.exe", @"C:\Program Files (x86)\Google\x.exe")]
    public void User_variables_name_the_signed_in_users_folders(string pattern, string expected) =>
        Assert.Equal(expected, ActiveUser.Expand(pattern, Alice, ServiceVariable));

    [Fact]
    public void Without_a_user_the_user_variables_stay_unexpanded()
    {
        // PathPatterns skips a pattern with a '%' left, so these match nothing instead of SYSTEM's own profile.
        Assert.Equal(@"%LOCALAPPDATA%\Google\x.exe", ActiveUser.Expand(@"%LOCALAPPDATA%\Google\x.exe", null, ServiceVariable));
        Assert.Equal(@"%APPDATA%\x.exe", ActiveUser.Expand(@"%APPDATA%\x.exe", Alice with { RoamingAppData = null }, ServiceVariable));
    }

    [Fact]
    public void An_unknown_variable_stays_and_a_lone_percent_is_kept() =>
        Assert.Equal(@"%NOPE%\50%\x", ActiveUser.Expand(@"%NOPE%\50%\x", Alice, ServiceVariable));

    [Fact]
    public void A_value_is_never_expanded_twice()
    {
        var odd = Alice with { LocalAppData = @"C:\Users\a%PROGRAMFILES(X86)%b\AppData\Local" };

        Assert.Equal(@"C:\Users\a%PROGRAMFILES(X86)%b\AppData\Local\x.exe", ActiveUser.Expand(@"%LOCALAPPDATA%\x.exe", odd, ServiceVariable));
    }

    [Fact]
    public void The_user_is_read_again_only_every_30_seconds()
    {
        var time = new FakeTimeProvider();
        var reads = 0;
        var user = new ActiveUser(new ListLog(), time, () =>
        {
            reads++;
            return Alice;
        });

        Assert.Equal(Alice, user.Current);
        time.Advance(ActiveUser.RereadEvery - TimeSpan.FromSeconds(1));
        _ = user.Current;
        Assert.Equal(1, reads);

        time.Advance(TimeSpan.FromSeconds(1));
        _ = user.Current;
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_failed_read_counts_as_no_user_and_is_logged_once()
    {
        var time = new FakeTimeProvider();
        var log = new ListLog();
        var user = new ActiveUser(log, time, () => throw new InvalidOperationException("WTS broke"));

        Assert.Null(user.Current);
        time.Advance(ActiveUser.RereadEvery);
        Assert.Null(user.Current);

        Assert.Single(log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Equal(@"%LOCALAPPDATA%\x", user.ExpandUserEnv(@"%LOCALAPPDATA%\x"));
    }

    [Fact]
    public void Outside_a_service_it_falls_back_to_this_process_user()
    {
        // Not SYSTEM, so WTSQueryUserToken fails and the current account's own folders are used.
        var profile = ActiveUser.Read();

        Assert.NotNull(profile);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, profile.Sid);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile.LocalAppData, ignoreCase: true);
    }
}
