using ConnectionManager.Core.Qos;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Engine;

/// The engine's view of UploadCapManager: the caps and their switch come from the settings. The engine calls it on its
/// upload worker, one call at a time, as the manager needs.
public sealed class UploadCapController : IUploadCaps
{
    private readonly UploadCapManager _caps;

    public UploadCapController(UploadCapManager caps) => _caps = caps;

    public string? Apply(AppSettings settings) => _caps.Apply(settings.UploadCaps, settings.UploadCapsEnabled);

    public string? RemoveAll() => _caps.RemoveAll();
}
