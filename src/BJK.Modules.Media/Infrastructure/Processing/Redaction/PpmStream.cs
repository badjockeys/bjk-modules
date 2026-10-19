using System.Text;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Reads and writes binary PPM (P6) pictures: a short text header then raw RGB bytes. ffmpeg decodes frames to this
/// over a pipe (<c>-f image2pipe -c:v ppm</c>), which tells us each frame's size without any image library.
/// </summary>
internal sealed class PpmReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _position;
    private int _length;

    /// <summary>The next picture, or null at the end of the stream.</summary>
    public async Task<RgbFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        var magic = await ReadTokenAsync(cancellationToken);
        if (magic is null)
        {
            return null;
        }

        if (magic != "P6")
        {
            throw new UnreadableMediaException($"Expected a PPM picture, got '{magic}'.");
        }

        var width = await ReadNumberAsync(cancellationToken);
        var height = await ReadNumberAsync(cancellationToken);
        var maxValue = await ReadNumberAsync(cancellationToken);
        if (maxValue != 255 || width <= 0 || height <= 0)
        {
            throw new UnreadableMediaException("Unsupported PPM picture.");
        }

        // Exactly one whitespace byte separates the header from the pixels; ReadTokenAsync consumed it.
        var frame = new RgbFrame(width, height);
        var offset = 0;
        var available = Math.Min(_length - _position, frame.Pixels.Length);
        if (available > 0)
        {
            Buffer.BlockCopy(_buffer, _position, frame.Pixels, 0, available);
            _position += available;
            offset = available;
        }

        if (offset < frame.Pixels.Length)
        {
            try
            {
                await stream.ReadExactlyAsync(frame.Pixels.AsMemory(offset), cancellationToken);
            }
            catch (EndOfStreamException ex)
            {
                throw new UnreadableMediaException("The picture data ended early.", ex);
            }
        }

        return frame;
    }

    private async Task<int> ReadNumberAsync(CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(cancellationToken);
        return int.TryParse(token, out var value)
            ? value
            : throw new UnreadableMediaException("Unreadable PPM header.");
    }

    /// <summary>Skips whitespace and # comments, reads one token and the single whitespace byte after it.</summary>
    private async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var token = new StringBuilder();
        while (true)
        {
            var b = await ReadByteAsync(cancellationToken);
            if (b < 0)
            {
                return token.Length == 0 ? null : token.ToString();
            }

            if (b == '#' && token.Length == 0)
            {
                while (b >= 0 && b != '\n')
                {
                    b = await ReadByteAsync(cancellationToken);
                }

                continue;
            }

            if (b is ' ' or '\n' or '\r' or '\t')
            {
                if (token.Length > 0)
                {
                    return token.ToString();
                }

                continue;
            }

            if (token.Length > 16)
            {
                throw new UnreadableMediaException("Unreadable PPM header.");
            }

            token.Append((char)b);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position >= _length)
        {
            _length = await stream.ReadAsync(_buffer, cancellationToken);
            _position = 0;
            if (_length == 0)
            {
                return -1;
            }
        }

        return _buffer[_position++];
    }
}

internal static class PpmWriter
{
    /// <summary>Writes the frame as a binary PPM file.</summary>
    public static async Task WriteAsync(RgbFrame frame, string path, CancellationToken cancellationToken)
    {
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"), cancellationToken);
        await file.WriteAsync(frame.Pixels.AsMemory(0, frame.Stride * frame.Height), cancellationToken);
    }
}
