using System.Buffers;
using System.Text;
using System.Text.Json;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Bridge;

/// What the settings window does with one message from its page.
public enum BridgeRouteKind
{
    Drop,           // not from our page, too big, or no id to answer: no reply at all
    Reject,         // answer { id, ok: false, error }
    Forward,        // send Request to the service and answer with its reply
    PickProgram,    // the rest are handled by the tray itself
    PickFolder,
    OpenLogFolder,
}

/// Request is set for Forward, Error for Reject.
public sealed record BridgeRoute(BridgeRouteKind Kind, int Id = 0, Request? Request = null, string? Error = null);

/// The settings page (web/, in the tray's WebView2) posts { id, cmd, args? }. These checks are pure, so the window stays a
/// thin shell: the sender must be our virtual host, the message small and strict, the command on a fixed list. Like any
/// pipe client, the page's arguments are left to the service to validate.
public static class BridgeRouter
{
    /// RFC 6761 reserves .example, so this can never be a real site.
    public const string Host = "app.connectionmanager.example";
    public const string Origin = "https://" + Host;
    public const int MaxMessageBytes = 64 * 1024;
    private const int MaxExternalUrl = 2048;

    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 8, AllowDuplicateProperties = false }; // as IpcJson
    private static readonly BridgeRoute Dropped = new(BridgeRouteKind.Drop);

    private static readonly Dictionary<string, BridgeRouteKind> TrayCommands = new(StringComparer.Ordinal)
    {
        ["pickProgram"] = BridgeRouteKind.PickProgram,
        ["pickFolder"] = BridgeRouteKind.PickFolder,
        ["openLogFolder"] = BridgeRouteKind.OpenLogFolder,
    };

    /// https, our host, the default port and no user info. A path, about:blank or file: is not ours.
    public static bool IsOurOrigin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase)
        && u.IsDefaultPort
        && u.UserInfo.Length == 0;

    /// The settings page, opened on a tab ("reports") when one is given.
    public static string PageUrl(string? tab) => Origin + "/index.html" + (string.IsNullOrEmpty(tab) ? "" : "#" + Uri.EscapeDataString(tab));

    /// source is CoreWebView2WebMessageReceivedEventArgs.Source (the full document URL), json is WebMessageAsJson.
    public static BridgeRoute Route(string? source, string? json)
    {
        if (!IsOurOrigin(source) || json is null) return Dropped;
        // Every char is at least one UTF-8 byte, so the cheap check first.
        if (json.Length > MaxMessageBytes || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) return Dropped;
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            return Route(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) // ArgumentException: a lone surrogate
        {
            return Dropped;
        }
    }

    private static BridgeRoute Route(JsonElement root)
    {
        // Without an id there is no one to answer; id 0 marks a status push.
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number
            || !idValue.TryGetInt32(out var id) || id < 1)
            return Dropped;

        JsonElement? cmd = null, args = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    break;
                case "cmd":
                    cmd = property.Value;
                    break;
                case "args" when property.Value.ValueKind == JsonValueKind.Object:
                    args = property.Value.Clone(); // outlives the document
                    break;
                case "args":
                    return Reject(id, "Bad arguments.");
                default:
                    return Reject(id, "Unexpected field in the message."); // never echo what the page sent
            }
        }
        if (cmd is not { ValueKind: JsonValueKind.String } name) return Reject(id, "Unknown command.");

        if (TrayCommands.TryGetValue(name.GetString()!, out var local))
            return args is null ? new BridgeRoute(local, id) : Reject(id, "Bad arguments.");
        if (!TryParseCommand(name, out var command)) return Reject(id, "Unknown command.");
        return new BridgeRoute(BridgeRouteKind.Forward, id, new Request(Framing.Version, id, command, args));
    }

    // The names the pipe accepts, through IpcJson's StrictEnum: "GetStatus", "getstatus" and 3 all fail. The tray reports
    // full-screen games itself, and the window gets its status pushes through the tray's own subscription.
    private static bool TryParseCommand(JsonElement name, out Command command)
    {
        try
        {
            command = name.Deserialize(IpcJson.Default.Command);
        }
        catch (JsonException)
        {
            command = default;
            return false;
        }
        return command is not (Command.ReportFullscreenGame or Command.Subscribe);
    }

    private static BridgeRoute Reject(int id, string error) => new(BridgeRouteKind.Reject, id, Error: error);

    /// The service's reply, under the page's id: the pipe numbers its own requests.
    public static string Reply(int id, Response response) => JsonSerializer.Serialize(response with { Id = id }, IpcJson.Wire.Response);

    public static string Error(int id, string error) => Reply(id, new Response(id, false, error));

    /// { path } for a pick, null data when the user cancelled.
    public static string Picked(int id, string? path)
    {
        if (path is null) return Reply(id, new Response(id, true));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteEndObject();
        }
        using var data = JsonDocument.Parse(buffer.WrittenMemory);
        return Reply(id, new Response(id, true, Data: data.RootElement));
    }

    /// A status pushed to the page: { id: 0, ok: true, data: StatusDto }.
    public static string Push(StatusDto status) =>
        Reply(0, new Response(0, true, Data: JsonSerializer.SerializeToElement(status, IpcJson.Default.StatusDto)));

    /// A link the page asked to open in a new window, made safe to hand to explorer.exe: https only, no user info.
    /// Null when it may not open. Commas are escaped because explorer.exe splits its command line on them.
    public static string? ExternalUrl(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || u.Host.Length == 0)
            return null;
        var url = u.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal);
        return url.Length <= MaxExternalUrl ? url : null;
    }
}
