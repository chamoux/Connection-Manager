using System.Buffers.Binary;
using System.Text;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Tests;

public sealed class FramingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_frame_round_trips()
    {
        using var stream = new MemoryStream();
        var json = Encoding.UTF8.GetBytes("""{"v":1}""");

        await Framing.WriteFrameAsync(stream, json, Ct);
        await Framing.WriteFrameAsync(stream, json, Ct);
        stream.Position = 0;

        Assert.Equal(json, await Framing.ReadFrameAsync(stream, Ct));
        Assert.Equal(json, await Framing.ReadFrameAsync(stream, Ct));
        Assert.Null(await Framing.ReadFrameAsync(stream, Ct)); // clean end between frames
    }

    [Fact]
    public async Task A_frame_is_one_write_with_a_little_endian_length()
    {
        var stream = new CountingStream();

        await Framing.WriteFrameAsync(stream, new byte[300], Ct);

        Assert.Equal(1, stream.Writes);
        Assert.Equal(304, stream.Length);
        Assert.Equal(new byte[] { 0x2C, 0x01, 0, 0 }, stream.ToArray()[..4]);
    }

    [Fact]
    public async Task The_largest_frame_is_accepted()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, new byte[IpcLimits.MaxFrame], Ct);
        stream.Position = 0;

        Assert.Equal(IpcLimits.MaxFrame, (await Framing.ReadFrameAsync(stream, Ct))!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(IpcLimits.MaxFrame + 1)]
    [InlineData(-1)]
    public async Task A_zero_negative_or_oversize_length_is_refused(int length)
    {
        using var stream = Header(length);

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(IpcLimits.MaxFrame + 1)]
    public async Task An_empty_or_oversize_frame_is_never_written(int length)
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.WriteFrameAsync(stream, new byte[length], Ct));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task A_server_frame_may_be_larger_than_a_client_frame()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, new byte[IpcLimits.MaxServerFrame], IpcLimits.MaxServerFrame, Ct);
        stream.Position = 0;

        Assert.Equal(IpcLimits.MaxServerFrame, (await Framing.ReadFrameAsync(stream, IpcLimits.MaxServerFrame, Ct))!.Length);
    }

    [Fact]
    public async Task The_default_limit_stays_the_client_limit()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, new byte[IpcLimits.MaxFrame + 1], IpcLimits.MaxServerFrame, Ct);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream, Ct)); // what the service reads from clients
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.WriteFrameAsync(new MemoryStream(), new byte[IpcLimits.MaxFrame + 1], Ct));
    }

    [Fact]
    public async Task A_frame_over_the_server_limit_is_refused_both_ways()
    {
        using var stream = Header(IpcLimits.MaxServerFrame + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream, IpcLimits.MaxServerFrame, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Framing.WriteFrameAsync(new MemoryStream(), new byte[IpcLimits.MaxServerFrame + 1], IpcLimits.MaxServerFrame, Ct));
    }

    [Fact]
    public async Task An_end_inside_the_header_is_refused()
    {
        using var stream = new MemoryStream([5, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream, Ct));
    }

    [Fact]
    public async Task An_end_inside_the_payload_is_refused()
    {
        using var stream = Header(10, payload: [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream, Ct));
    }

    private static MemoryStream Header(int length, byte[]? payload = null)
    {
        var bytes = new byte[4 + (payload?.Length ?? 0)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        payload?.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Writes++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
