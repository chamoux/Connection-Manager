using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Json.Serialization.Metadata;

namespace ConnectionManager.Core.Ipc;

/// A message on the pipe is a 4-byte little-endian length, then that many bytes of JSON.
/// Error messages here go back to the client and into logs, so they never repeat what the client sent.
public static class Framing
{
    public const int Version = 1;

    /// One write, so two writers on the same stream never interleave. At most IpcLimits.MaxFrame bytes.
    public static Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> json, CancellationToken ct) => WriteFrameAsync(s, json, IpcLimits.MaxFrame, ct);

    /// The service's replies and status pushes use IpcLimits.MaxServerFrame.
    public static async Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> json, int maxBytes, CancellationToken ct)
    {
        CheckLength(json.Length, maxBytes);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsMemory(4));
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    /// Null when the other end closed cleanly between frames. At most IpcLimits.MaxFrame bytes: what the service reads.
    public static Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct) => ReadFrameAsync(s, IpcLimits.MaxFrame, ct);

    /// Clients read the service's frames with IpcLimits.MaxServerFrame.
    public static async Task<byte[]?> ReadFrameAsync(Stream s, int maxBytes, CancellationToken ct)
    {
        var header = new byte[4];
        var got = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (got == 0) return null;
        if (got < header.Length) throw new InvalidDataException("The connection ended inside a frame header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        CheckLength(length, maxBytes); // before allocating anything
        var body = new byte[length];
        if (await s.ReadAtLeastAsync(body, length, throwOnEndOfStream: false, ct) < length)
            throw new InvalidDataException("The connection ended inside a frame.");
        return body;
    }

    private static void CheckLength(int length, int maxBytes)
    {
        if (length < 1 || length > maxBytes) throw new InvalidDataException($"A frame of {length} bytes is out of range.");
    }

    public static byte[] Serialize(Request request) => JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Wire.Request);

    public static byte[] Serialize(Response response) => JsonSerializer.SerializeToUtf8Bytes(response, IpcJson.Wire.Response);

    public static bool TryParseRequest(byte[] frame, [NotNullWhen(true)] out Request? request, out string error)
    {
        request = null;
        Request? parsed = null;
        try
        {
            // The reader leaves strings inside args unchecked until they are read, so check the whole frame now.
            if (Utf8.IsValid(frame)) parsed = JsonSerializer.Deserialize(frame, IpcJson.Default.Request);
        }
        catch (JsonException)
        {
            // reported as malformed below
        }

        error = parsed switch
        {
            null => "Malformed request.",
            { V: not Version } => "Unsupported protocol version.",
            { Id: < 1 } => "Bad request id.", // 0 marks pushed status
            _ => "",
        };
        if (error.Length > 0) return false;
        request = parsed!;
        return true;
    }

    /// The request's arguments as T, with the length limits checked. Throws InvalidDataException with a short message.
    public static T ParseArgs<T>(Request request) where T : class
    {
        var info = IpcJson.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not in IpcJson.");
        if (request.Args is not { ValueKind: JsonValueKind.Object } args) throw new InvalidDataException($"Missing arguments for {request.Cmd}.");

        T? value;
        try
        {
            value = args.Deserialize(info);
        }
        catch (JsonException)
        {
            value = null;
        }
        if (value is null) throw new InvalidDataException($"Bad arguments for {request.Cmd}.");
        if (Problem(value) is { } problem) throw new InvalidDataException(problem);
        return value;
    }

    private static string? Problem(object args) => args switch
    {
        PathArgs a => PathProblem(a.Path),
        GameArgs a => a.Pid < 0 ? "Bad process id." : PathProblem(a.Path),
        UploadCapArgs a => PathProblem(a.Path),
        IdArgs a => TextProblem(a.Id, IpcLimits.MaxName, "id"),
        HotspotArgs a => TextProblem(a.Name, IpcLimits.MaxName, "network name"),
        OptionsDto a => TextProblem(a.Hotkey, IpcLimits.MaxHotkey, "hotkey"),
        _ => null,
    };

    private static string? PathProblem(string path) => TextProblem(path, IpcLimits.MaxPath, "path");

    private static string? TextProblem(string text, int max, string what) =>
        string.IsNullOrWhiteSpace(text) ? $"The {what} is empty." : text.Length > max ? $"The {what} is too long." : null;
}
