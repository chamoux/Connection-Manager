using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;

// As in the src projects: a P/Invoke in this assembly loads its DLL from System32 only, never from the app or current folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

// Dev tool for the smoke test: sends one command to the installed service over its pipe. PipeClient checks the server
// the way the tray does (owner SYSTEM, session 0, the ConnectionManager service's own process), so this only ever
// talks to the real service. `status` prints the status as JSON; the set commands print a one-line summary.
// Exit codes: 0 done, 1 refused or no service, 2 bad arguments.
const string Usage = "usage: ConnectionManager.IpcProbe status | set-game auto|on|off | set-saver auto|on|off";
var connectTimeout = TimeSpan.FromSeconds(5);

Command? command = null;
ModeArgs? modeArgs = null;
switch (args)
{
    case ["status"]:
        command = Command.GetStatus;
        break;
    case ["set-game", var text] when ParseMode(text) is { } mode:
        command = Command.SetGameMode;
        modeArgs = new ModeArgs(mode);
        break;
    case ["set-saver", var text] when ParseMode(text) is { } mode:
        command = Command.SetDataSaver;
        modeArgs = new ModeArgs(mode);
        break;
}
if (command is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

await using var client = new PipeClient();
try
{
    await client.ConnectAsync(connectTimeout, CancellationToken.None);
}
catch (TimeoutException)
{
    Console.Error.WriteLine($"The Connection Manager service is not running: nothing answered on its pipe within {connectTimeout.TotalSeconds:0} s.");
    return 1;
}
catch (PipeServerVerificationException ex)
{
    Console.Error.WriteLine($"Refused to talk to the pipe: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not connect to the Connection Manager service: {ex.Message}");
    return 1;
}

Response response;
try
{
    response = await client.SendAsync(command.Value, modeArgs, CancellationToken.None);
}
catch (Exception ex) when (ex is IOException or TimeoutException)
{
    Console.Error.WriteLine($"The service did not answer: {ex.Message}");
    return 1;
}

if (!response.Ok)
{
    Console.Error.WriteLine($"The service refused: {response.Error}");
    return 1;
}
if (command == Command.GetStatus)
{
    Console.WriteLine(response.Data is { } data ? Indented(data) : "{}");
}
else
{
    var status = response.Data?.Deserialize(IpcJson.Default.StatusDto);
    Console.WriteLine(status is null
        ? "ok"
        : $"ok: Game Mode {status.GameMode} (active: {status.GameActive}), Data Saver {status.DataSaver} (active: {status.SaverActive})");
}
return 0;

static ModeSetting? ParseMode(string text) => text switch
{
    "auto" => ModeSetting.Auto,
    "on" => ModeSetting.On,
    "off" => ModeSetting.Off,
    _ => null,
};

static string Indented(JsonElement element)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        element.WriteTo(writer);
    }
    return Encoding.UTF8.GetString(stream.ToArray());
}
