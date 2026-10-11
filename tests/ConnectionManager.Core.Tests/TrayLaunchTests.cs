using ConnectionManager.Core.Tray;

namespace ConnectionManager.Core.Tests;

public class TrayLaunchTests
{
    private static string UniqueName() => $@"Local\ConnectionManager.Test.{Guid.NewGuid():N}";

    [Fact]
    public void A_start_from_the_Start_menu_opens_the_window() => Assert.True(TrayLaunch.OpensWindow([]));

    [Fact]
    public void The_sign_in_start_stays_in_the_tray() => Assert.False(TrayLaunch.OpensWindow([TrayLaunch.Background]));

    [Theory]
    [InlineData("--BACKGROUND")]
    [InlineData("/background")]
    [InlineData("background")]
    public void Anything_else_opens_the_window(string argument) => Assert.True(TrayLaunch.OpensWindow([argument]));

    [Fact]
    public void Extra_arguments_open_the_window() => Assert.True(TrayLaunch.OpensWindow([TrayLaunch.Background, "x"]));

    [Fact]
    public void The_event_name_is_per_session_and_per_user()
    {
        Assert.Equal(@"Local\ConnectionManager.Tray.S-1-5-21-1-2-3-1001.Open", TrayLaunch.OpenEventName("S-1-5-21-1-2-3-1001"));
    }

    [Fact]
    public void A_second_copy_can_ask_the_running_one_to_open_its_window()
    {
        var name = UniqueName();
        using var opened = new ManualResetEventSlim();
        using var listener = new OpenRequests(name, opened.Set);

        Assert.True(OpenRequests.Send(name));

        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Asking_when_nothing_is_running_says_so() => Assert.False(OpenRequests.Send(UniqueName()));

    [Fact]
    public void Every_request_is_heard_and_a_closed_listener_hears_none()
    {
        var name = UniqueName();
        var count = 0;
        using var heard = new SemaphoreSlim(0);
        var listener = new OpenRequests(name, () =>
        {
            Interlocked.Increment(ref count);
            heard.Release();
        });

        Assert.True(OpenRequests.Send(name));
        Assert.True(heard.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(OpenRequests.Send(name));
        Assert.True(heard.Wait(TimeSpan.FromSeconds(5)));
        listener.Dispose();

        Assert.False(OpenRequests.Send(name)); // the name went with it
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_failing_handler_does_not_take_the_process_down()
    {
        var name = UniqueName();
        using var heard = new SemaphoreSlim(0);
        var calls = 0;
        using var listener = new OpenRequests(name, () =>
        {
            heard.Release();
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
        });

        Assert.True(OpenRequests.Send(name));
        Assert.True(heard.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(OpenRequests.Send(name));
        Assert.True(heard.Wait(TimeSpan.FromSeconds(5))); // still listening after the throw
    }
}
