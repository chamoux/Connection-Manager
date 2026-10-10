using System.Text.Json;
using ConnectionManager.Core.Bridge;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

public sealed class BridgeRouterTests
{
    private const string Page = "https://app.connectionmanager.example/index.html#status";

    private static BridgeRoute Route(string json, string? source = Page) => BridgeRouter.Route(source, json);

    private static void Dropped(string json, string? source = Page) => Assert.Equal(BridgeRouteKind.Drop, Route(json, source).Kind);

    private static string Rejected(string json, int id = 4)
    {
        var route = Route(json);
        Assert.Equal((BridgeRouteKind.Reject, id, (Request?)null), (route.Kind, route.Id, route.Request));
        Assert.InRange(route.Error!.Length, 1, 100);
        return route.Error;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/index.html")]
    [InlineData("index.html")]
    [InlineData("about:blank")]
    [InlineData("http://app.connectionmanager.example/index.html")]
    [InlineData("file:///C:/Program%20Files/ConnectionManager/ui/index.html")]
    [InlineData("https://evil.example/index.html")]
    [InlineData("https://app.connectionmanager.example.evil.example/index.html")]
    [InlineData("https://app.connectionmanager.example:8443/index.html")]
    [InlineData("https://user@app.connectionmanager.example/index.html")]
    public void A_message_from_anywhere_but_our_page_is_dropped(string? source)
    {
        Assert.False(BridgeRouter.IsOurOrigin(source));
        Dropped("""{"id":1,"cmd":"getStatus"}""", source);
    }

    [Theory]
    [InlineData("https://app.connectionmanager.example/index.html")]
    [InlineData("https://APP.connectionmanager.example/index.html#reports")]
    [InlineData("https://app.connectionmanager.example:443/assets/x.js")]
    public void Our_page_is_our_origin(string source) => Assert.True(BridgeRouter.IsOurOrigin(source));

    [Fact]
    public void An_oversize_message_is_dropped()
    {
        var padding = new string('a', BridgeRouter.MaxMessageBytes);
        Dropped($$$"""{"id":1,"cmd":"addBlock","args":{"path":"{{{padding}}}"}}""");
    }

    [Fact]
    public void The_size_cap_counts_utf8_bytes()
    {
        var padding = new string('é', BridgeRouter.MaxMessageBytes / 2); // 2 bytes each, so over the cap with the rest
        Dropped($$$"""{"id":1,"cmd":"addBlock","args":{"path":"{{{padding}}}"}}""");
    }

    [Theory]
    [InlineData("""{"id":0,"cmd":"getStatus"}""")]
    [InlineData("""{"id":-3,"cmd":"getStatus"}""")]
    [InlineData("""{"id":1.5,"cmd":"getStatus"}""")]
    [InlineData("""{"id":"1","cmd":"getStatus"}""")]
    [InlineData("""{"id":2147483648,"cmd":"getStatus"}""")]
    [InlineData("""{"cmd":"getStatus"}""")]
    [InlineData("""{"id":1,"id":2,"cmd":"getStatus"}""")]
    [InlineData("""[1,"getStatus"]""")]
    [InlineData("\"getStatus\"")]
    [InlineData("""{"id":1,"cmd":"getStatus" """)]
    [InlineData("""{"id":1,"cmd":"getStatus",}""")]
    public void A_message_without_a_usable_id_is_dropped(string json) => Dropped(json);

    [Fact]
    public void Text_that_is_not_valid_utf16_is_dropped() => Dropped("{\"id\":1,\"cmd\":\"getStatus\",\"args\":{\"path\":\"\uD800\"}}");

    [Theory]
    [InlineData("""{"id":4,"cmd":"fooBar"}""")]
    [InlineData("""{"id":4,"cmd":"GetStatus"}""")]
    [InlineData("""{"id":4,"cmd":"getstatus"}""")]
    [InlineData("""{"id":4,"cmd":" getStatus"}""")]
    [InlineData("""{"id":4,"cmd":"PickProgram"}""")]
    [InlineData("""{"id":4,"cmd":""}""")]
    [InlineData("""{"id":4}""")]
    public void An_unknown_command_is_rejected(string json) => Rejected(json);

    [Theory]
    [InlineData("reportFullscreenGame", """{"pid":1,"path":"C:\\Games\\x.exe"}""")]
    [InlineData("subscribe", null)]
    public void The_page_may_not_send_tray_only_commands(string cmd, string? args) =>
        Rejected(args is null ? $$"""{"id":4,"cmd":"{{cmd}}"}""" : $$"""{"id":4,"cmd":"{{cmd}}","args":{{args}}}""");

    [Theory]
    [InlineData("""{"id":4,"cmd":2}""")]
    [InlineData("""{"id":4,"cmd":"2"}""")]
    [InlineData("""{"id":4,"cmd":null}""")]
    [InlineData("""{"id":4,"cmd":["getStatus"]}""")]
    public void A_command_that_is_not_a_name_is_rejected(string json) => Rejected(json);

    [Theory]
    [InlineData("""{"id":4,"cmd":"setGameMode","args":"on"}""")]
    [InlineData("""{"id":4,"cmd":"setGameMode","args":["on"]}""")]
    [InlineData("""{"id":4,"cmd":"setGameMode","args":null}""")]
    [InlineData("""{"id":4,"cmd":"pickProgram","args":{}}""")]
    public void Arguments_that_are_not_an_object_are_rejected(string json) => Rejected(json);

    [Theory]
    [InlineData("""{"id":4,"cmd":"getStatus","v":1}""")]
    [InlineData("""{"id":4,"cmd":"getStatus","$type":"x"}""")]
    public void An_unexpected_field_is_rejected(string json) => Rejected(json);

    [Fact]
    public void A_service_command_is_forwarded_with_its_arguments_unchanged()
    {
        var route = Route("""{"id":7,"cmd":"setGameMode","args":{"mode":"on", "extra":[1,2]}}""");

        Assert.Equal((BridgeRouteKind.Forward, 7, (string?)null), (route.Kind, route.Id, route.Error));
        var request = route.Request!;
        Assert.Equal((Framing.Version, 7, Command.SetGameMode), (request.V, request.Id, request.Cmd));
        Assert.Equal("""{"mode":"on", "extra":[1,2]}""", request.Args!.Value.GetRawText()); // the service validates them
    }

    [Fact]
    public void A_command_without_arguments_is_forwarded_without_them()
    {
        var route = Route("""{"id":1,"cmd":"getStatus"}""");

        Assert.Equal(BridgeRouteKind.Forward, route.Kind);
        Assert.Equal(new Request(1, 1, Command.GetStatus), route.Request);
    }

    [Fact]
    public void Every_service_command_but_the_tray_only_ones_is_forwarded()
    {
        foreach (var command in Enum.GetValues<Command>())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(command.ToString());
            var route = Route($$"""{"id":4,"cmd":"{{name}}"}""");
            var trayOnly = command is Command.ReportFullscreenGame or Command.Subscribe;
            Assert.Equal(trayOnly ? BridgeRouteKind.Reject : BridgeRouteKind.Forward, route.Kind);
            if (!trayOnly) Assert.Equal(command, route.Request!.Cmd);
        }
    }

    [Theory]
    [InlineData("pickProgram", BridgeRouteKind.PickProgram)]
    [InlineData("pickFolder", BridgeRouteKind.PickFolder)]
    [InlineData("openLogFolder", BridgeRouteKind.OpenLogFolder)]
    public void Bridge_only_commands_stay_in_the_tray(string cmd, BridgeRouteKind kind)
    {
        var route = Route($$"""{"id":9,"cmd":"{{cmd}}"}""");

        Assert.Equal((kind, 9, (Request?)null, (string?)null), (route.Kind, route.Id, route.Request, route.Error));
    }

    [Fact]
    public void Forwarded_arguments_survive_the_pipe_clients_serialization()
    {
        // PipeClient.SendAsync serializes args through IpcJson by their runtime type, here a boxed JsonElement.
        var args = Route("""{"id":1,"cmd":"addBlock","args":{"path":"C:\\x.exe","isFolder":false}}""").Request!.Args;
        object boxed = args!.Value;
        var info = IpcJson.Default.GetTypeInfo(boxed.GetType());

        Assert.NotNull(info);
        var request = new Request(1, 5, Command.AddBlock, JsonSerializer.SerializeToElement(boxed, info));
        Assert.True(Framing.TryParseRequest(Framing.Serialize(request), out var parsed, out _));
        Assert.Equal(new PathArgs(@"C:\x.exe"), Framing.ParseArgs<PathArgs>(parsed));
    }

    [Fact]
    public void A_service_reply_goes_back_under_the_pages_id()
    {
        var data = JsonSerializer.SerializeToElement(new IdArgs("r1"), IpcJson.Default.IdArgs);

        Assert.Equal("""{"id":12,"ok":true,"error":null,"data":{"id":"r1"}}""", BridgeRouter.Reply(12, new Response(3, true, Data: data)));
        Assert.Equal("""{"id":12,"ok":false,"error":"No.","data":null}""", BridgeRouter.Reply(12, new Response(3, false, "No.")));
        Assert.Equal("""{"id":5,"ok":false,"error":"Unknown command.","data":null}""", BridgeRouter.Error(5, "Unknown command."));
    }

    [Fact]
    public void A_pick_answers_with_the_path_or_null()
    {
        Assert.Equal("""{"id":3,"ok":true,"error":null,"data":{"path":"C:\\Games\\x.exe"}}""", BridgeRouter.Picked(3, @"C:\Games\x.exe"));
        Assert.Equal("""{"id":3,"ok":true,"error":null,"data":null}""", BridgeRouter.Picked(3, null));
    }

    [Fact]
    public void A_status_push_has_id_zero()
    {
        var options = new OptionsDto(2, true, "Ctrl+Alt+G", true, false, 200, true, true, true, true);
        var settings = new SettingsDto(options, [], [new UserBlock(@"C:\x.exe", false)], [], [], [], [], []);
        var status = new StatusDto(3, ModeSetting.Auto, false, null, null, [], 0, ModeSetting.Auto, false, "", 0,
            new NetworkDto("none", null, false, false, null, false, false), [], settings, null);

        var push = JsonSerializer.Deserialize(BridgeRouter.Push(status), IpcJson.Default.Response)!;

        Assert.Equal((0, true), (push.Id, push.Ok));
        Assert.Equal(status.Version, push.Data!.Value.Deserialize(IpcJson.Default.StatusDto)!.Version);
    }

    [Theory]
    [InlineData(null, "https://app.connectionmanager.example/index.html")]
    [InlineData("reports", "https://app.connectionmanager.example/index.html#reports")]
    public void The_page_url_carries_the_tab(string? tab, string url)
    {
        Assert.Equal(url, BridgeRouter.PageUrl(tab));
        Assert.True(BridgeRouter.IsOurOrigin(url));
    }

    [Theory]
    [InlineData("https://example.com/help", "https://example.com/help")]
    [InlineData("https://example.com/a,C:\\Windows\\System32\\calc.exe", "https://example.com/a%2CC:/Windows/System32/calc.exe")]
    [InlineData("http://example.com/", null)]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData("ms-settings:network", null)]
    [InlineData("https://user:pw@example.com/", null)]
    [InlineData("/relative", null)]
    [InlineData(null, null)]
    public void Only_https_links_open_in_the_browser(string? uri, string? expected) => Assert.Equal(expected, BridgeRouter.ExternalUrl(uri));
}
