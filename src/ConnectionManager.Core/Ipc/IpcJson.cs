using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Ipc;

/// Strict JSON for the pipe: the other end is an untrusted local process.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(StrictEnum<Command>), typeof(StrictEnum<ModeSetting>)], // every enum in the contracts
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = 8)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(ModeArgs))]
[JsonSerializable(typeof(PathArgs))]
[JsonSerializable(typeof(IdArgs))]
[JsonSerializable(typeof(HotspotArgs))]
[JsonSerializable(typeof(OnArgs))]
[JsonSerializable(typeof(GameArgs))]
[JsonSerializable(typeof(UploadCapArgs))]
[JsonSerializable(typeof(OptionsDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(IReadOnlyList<UsageDto>))]
[JsonSerializable(typeof(IReadOnlyList<ReportSummaryDto>))]
[JsonSerializable(typeof(ReportDto))]
[JsonSerializable(typeof(IReadOnlyList<ReportDto>))]
public sealed partial class IpcJson : JsonSerializerContext
{
    private static IpcJson? _wire;

    /// For what goes out (frames, the web UI, the status size): the same options, but non-ASCII text (a path in
    /// Cyrillic, say) goes as UTF-8 rather than as 6-byte escapes, so a status with long lists still fits a frame.
    /// Characters that matter to HTML or script (< > & ' " + `) are still escaped, as by default; the tray hands this
    /// JSON to the web UI as JSON (PostWebMessageAsJson), never inside HTML. A JsonElement made with Default is
    /// escaped again by the writer, so only the final serialization needs this. Built on first use: Default, from the
    /// generated half of this class, may not be set up yet while static fields here are.
    public static IpcJson Wire => _wire ??= new IpcJson(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
}
