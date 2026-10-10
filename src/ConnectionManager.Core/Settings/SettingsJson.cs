using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectionManager.Core.Settings;

/// settings.json is lenient where the pipe is strict: an admin may edit it by hand, and SettingsStore repairs what it reads.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    WriteIndented = true,
    RespectNullableAnnotations = false, // nulls are repaired instead of rejecting the whole file
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
