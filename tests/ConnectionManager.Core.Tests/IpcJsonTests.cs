using System.Text;
using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

public sealed class IpcJsonTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static Request Parse(string json)
    {
        Assert.True(Framing.TryParseRequest(Utf8(json), out var request, out var error), error);
        return request;
    }

    private static string Refused(string json)
    {
        Assert.False(Framing.TryParseRequest(Utf8(json), out var request, out var error));
        Assert.Null(request);
        Assert.InRange(error.Length, 1, 100);
        return error;
    }

    private static Request Req(string cmd, string args) => Parse($$"""{"v":1,"id":1,"cmd":"{{cmd}}","args":{{args}}}""");

    private static InvalidDataException BadArgs<T>(string args) where T : class
    {
        var ex = Assert.Throws<InvalidDataException>(() => Framing.ParseArgs<T>(Req("addBlock", args)));
        Assert.InRange(ex.Message.Length, 1, 100);
        return ex;
    }

    [Fact]
    public void Non_ascii_text_goes_as_utf8_and_html_characters_stay_escaped()
    {
        // A status full of non-ASCII paths would otherwise take 6 bytes a character and outgrow a frame.
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new IdArgs("Caf\u00E9 \u6E38\u620F <b>&'\u2028"), IpcJson.Wire.IdArgs));
        var frame = Encoding.UTF8.GetString(Framing.Serialize(new Response(1, true, null, JsonSerializer.SerializeToElement(new IdArgs("Caf\u00E9"), IpcJson.Default.IdArgs))));

        Assert.Equal("""{"id":"Café 游戏 \u003Cb\u003E\u0026\u0027\u2028"}""", json);
        Assert.Contains("\"Café\"", frame);
    }

    [Fact]
    public void A_request_round_trips_in_camel_case()
    {
        var args = JsonSerializer.SerializeToElement(new PathArgs(@"C:\Tools\x.exe", IsFolder: true), IpcJson.Default.PathArgs);
        var bytes = Framing.Serialize(new Request(1, 7, Command.AddBlock, args));

        Assert.Equal("""{"v":1,"id":7,"cmd":"addBlock","args":{"path":"C:\\Tools\\x.exe","isFolder":true}}""", Encoding.UTF8.GetString(bytes));
        Assert.True(Framing.TryParseRequest(bytes, out var request, out _));
        Assert.Equal((1, 7, Command.AddBlock), (request.V, request.Id, request.Cmd));
        Assert.Equal(new PathArgs(@"C:\Tools\x.exe", true), Framing.ParseArgs<PathArgs>(request));
    }

    [Fact]
    public void A_response_with_a_status_round_trips()
    {
        var options = new OptionsDto(2, true, "Ctrl+Alt+G", true, false, 200, true, true, true, true);
        var settings = new SettingsDto(options, [new CatalogItemDto("razer", "Razer", true)], [new UserBlock(@"C:\x.exe", false)],
            [], [], [], ["Phone"], [new UploadCapArgs(@"C:\obs.exe", 500)]);
        var status = new StatusDto(3, ModeSetting.On, true, "Game", DateTimeOffset.UnixEpoch, ["game.exe"], 4,
            ModeSetting.Auto, false, "", 0, new NetworkDto("wifi", "Phone", true, true, "list", false, false), [], settings, null);
        var data = JsonSerializer.SerializeToElement(status, IpcJson.Default.StatusDto);

        var text = Encoding.UTF8.GetString(Framing.Serialize(new Response(0, true, Data: data)));
        var back = JsonSerializer.Deserialize(text, IpcJson.Default.Response)!;

        Assert.Contains("\"gameMode\":\"on\"", text);
        Assert.Contains("\"kBps\":500", text);
        var parsed = back.Data!.Value.Deserialize(IpcJson.Default.StatusDto)!;
        Assert.Equal(status.GameMode, parsed.GameMode);
        Assert.Equal(status.Network, parsed.Network);
        Assert.Equal(options, parsed.Settings.Options);
        Assert.Equal(@"C:\obs.exe", Assert.Single(parsed.Settings.UploadCaps).Path);
    }

    [Theory]
    [InlineData("3")]               // an integer
    [InlineData("\"3\"")]           // an integer as a string
    [InlineData("\"99\"")]
    [InlineData("\"getStatus, subscribe\"")] // a flags-style list
    [InlineData("\"GetStatus\"")]   // names are camelCase only
    [InlineData("\"nope\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_command_must_be_a_known_name(string cmd) => Refused($$"""{"v":1,"id":1,"cmd":{{cmd}}}""");

    [Fact]
    public void An_integer_mode_is_refused_in_args() => BadArgs<ModeArgs>("""{"mode":1}""");

    [Fact]
    public void A_mode_is_read_by_name() =>
        Assert.Equal(ModeSetting.Off, Framing.ParseArgs<ModeArgs>(Req("setGameMode", """{"mode":"off"}""")).Mode);

    [Fact]
    public void An_unknown_property_is_refused() => Refused("""{"v":1,"id":1,"cmd":"getStatus","extra":1}""");

    [Fact]
    public void Property_names_match_case() => Refused("""{"V":1,"id":1,"cmd":"getStatus"}""");

    [Fact]
    public void A_duplicate_property_is_refused() => Refused("""{"v":1,"id":1,"cmd":"getStatus","cmd":"restoreEverything"}""");

    [Fact]
    public void A_duplicate_property_inside_args_is_refused() =>
        Refused("""{"v":1,"id":1,"cmd":"addBlock","args":{"path":"C:\\a.exe","path":"C:\\b.exe"}}""");

    [Fact]
    public void Eight_levels_of_nesting_are_accepted_and_nine_are_refused()
    {
        static string Nested(int levels) => // the request object is level 1
            """{"v":1,"id":1,"cmd":"getStatus","args":""" + new string('[', levels - 1) + new string(']', levels - 1) + "}";

        Parse(Nested(8));
        Refused(Nested(9));
    }

    [Fact]
    public void A_type_discriminator_is_refused()
    {
        Refused("""{"$type":"request","v":1,"id":1,"cmd":"getStatus"}""");
        BadArgs<PathArgs>("""{"$type":"x","path":"C:\\a.exe"}""");
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"getStatus"}""")]
    [InlineData("""{"v":1,"cmd":"getStatus"}""")]
    [InlineData("""{"v":1,"id":1}""")]
    [InlineData("""{"v":1,"id":"1","cmd":"getStatus"}""")]   // no numbers in strings
    [InlineData("""{"v":1,"id":1.5,"cmd":"getStatus"}""")]
    [InlineData("""{"v":1,"id":1,"cmd":"getStatus",}""")]    // trailing comma
    [InlineData("""{"v":1,"id":1,"cmd":"getStatus"} {}""")]  // two values
    [InlineData("""{"v":1,/**/"id":1,"cmd":"getStatus"}""")] // comment
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public void A_malformed_request_is_refused(string json) => Refused(json);

    [Fact]
    public void Invalid_utf8_is_refused_even_inside_args()
    {
        var bytes = Utf8("""{"v":1,"id":1,"cmd":"addBlock","args":{"path":"C:\\x"}}""");
        bytes[^4] = 0xFF; // the x

        Assert.False(Framing.TryParseRequest(bytes, out _, out _));
    }

    [Fact]
    public void A_lone_surrogate_in_args_gives_a_clean_error() => BadArgs<PathArgs>("""{"path":"C:\\\uD800.exe"}""");

    [Fact]
    public void Another_protocol_version_is_refused() => Assert.Contains("version", Refused("""{"v":2,"id":1,"cmd":"getStatus"}"""));

    [Theory]
    [InlineData(0)] // 0 is reserved for pushed status
    [InlineData(-1)]
    public void A_request_id_must_be_positive(int id) => Refused($$"""{"v":1,"id":{{id}},"cmd":"getStatus"}""");

    [Theory]
    [InlineData("")]
    [InlineData(""","args":null""")]
    public void Missing_args_give_a_clean_error(string args) =>
        Assert.Throws<InvalidDataException>(() => Framing.ParseArgs<PathArgs>(Parse($$"""{"v":1,"id":1,"cmd":"addBlock"{{args}}}""")));

    [Theory]
    [InlineData("\"C:\\\\x.exe\"")]
    [InlineData("[]")]
    [InlineData("{}")]                       // path is required
    [InlineData("""{"path":null}""")]
    [InlineData("""{"path":5}""")]
    [InlineData("""{"path":"C:\\x.exe","isFolder":"yes"}""")]
    [InlineData("""{"path":""}""")]
    [InlineData("""{"path":"   "}""")]
    public void Bad_path_args_give_a_clean_error(string args) => BadArgs<PathArgs>(args);

    [Fact]
    public void A_path_may_be_1024_characters_but_not_more()
    {
        var max = @"C:\" + new string('a', IpcLimits.MaxPath - 3);
        var json = max.Replace(@"\", @"\\");

        Assert.Equal(max, Framing.ParseArgs<PathArgs>(Req("addBlock", $$"""{"path":"{{json}}"}""")).Path);
        BadArgs<PathArgs>($$"""{"path":"{{json}}a"}""");
        BadArgs<GameArgs>($$"""{"pid":1,"path":"{{json}}a"}""");
        BadArgs<UploadCapArgs>($$"""{"path":"{{json}}a","kBps":100}""");
    }

    [Fact]
    public void Names_and_ids_are_capped_at_256_characters()
    {
        var name = new string('n', IpcLimits.MaxName);

        Assert.Equal(name, Framing.ParseArgs<IdArgs>(Req("toggleCatalog", $$"""{"id":"{{name}}"}""")).Id);
        BadArgs<IdArgs>($$"""{"id":"{{name}}n"}""");
        BadArgs<HotspotArgs>($$"""{"name":"{{name}}n","isHotspot":true}""");
        BadArgs<IdArgs>("""{"id":""}""");
        BadArgs<HotspotArgs>("""{"name":"","isHotspot":true}""");
    }

    [Fact]
    public void A_negative_process_id_is_refused() => BadArgs<GameArgs>("""{"pid":-1,"path":"C:\\g.exe"}""");

    [Fact]
    public void Options_must_be_a_whole_object_with_a_short_hotkey()
    {
        const string rest = """ "notifications":true,"autoOnMeteredWifi":true,"allowWindowsDownloads":false,"windowsDownloadCapKBps":200,"wifiTuning":true,"trafficMeter":true,"detectNonSteamGames":true,"uploadCapsEnabled":true""";

        var ok = Framing.ParseArgs<OptionsDto>(Req("setOptions", $$"""{"graceMinutes":2,"hotkey":"Ctrl+Alt+G",{{rest}}}"""));
        Assert.Equal("Ctrl+Alt+G", ok.Hotkey);
        BadArgs<OptionsDto>($$"""{"hotkey":"Ctrl+Alt+G",{{rest}}}""");                                   // graceMinutes missing
        BadArgs<OptionsDto>($$"""{"graceMinutes":2,"hotkey":"{{new string('x', IpcLimits.MaxHotkey + 1)}}",{{rest}}}""");
        BadArgs<OptionsDto>($$"""{"graceMinutes":2,"hotkey":"",{{rest}}}""");
    }

    [Fact]
    public void An_error_does_not_echo_the_payload()
    {
        var ex = BadArgs<PathArgs>($$"""{"path":"C:\\x","{{new string('z', 500)}}":1}""");

        Assert.DoesNotContain("zzz", ex.Message);
        Assert.DoesNotContain("zzz", Refused($$"""{"v":1,"id":1,"cmd":"getStatus","{{new string('z', 500)}}":1}"""));
    }

    [Fact]
    public void Every_enum_in_the_contracts_has_a_strict_converter()
    {
        var enums = typeof(Request).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(Request).Namespace)
            .SelectMany(t => t.GetProperties())
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => t.IsEnum)
            .Distinct()
            .ToList();

        Assert.Contains(typeof(Command), enums);
        Assert.Contains(typeof(ModeSetting), enums);
        foreach (var e in enums)
            Assert.Equal(typeof(StrictEnum<>).MakeGenericType(e), IpcJson.Default.GetTypeInfo(e)?.Converter.GetType());
    }
}
