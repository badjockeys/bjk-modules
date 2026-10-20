using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Safety;
using Xunit;

namespace BJK.Modules.Media.Tests.Safety;

/// <summary>
/// Our PDQ against Meta's reference implementation (the <c>pdqhash</c> Python package, version 0.2.7), on pictures
/// built the same way here. Reference hashes are in the standard 64-digit form. Pictures with many equal DCT
/// coefficients (smooth gradients) can differ by a few bits from float rounding, well inside a match (31).
/// </summary>
public sealed class PdqTests
{
    public static TheoryData<string, string, int, int> Cases => new()
    {
        { "noise_97x61_seed7", "6b343d576191f39cad39f81ee305f41233315078039d33e5503eaf38d707e651", 100, 2 },
        { "noise_250x180_seed42", "42e42e36ad484fa909c8385cf6cab731fb061b57aa669556a5bafa5af2215e81", 100, 2 },
        { "gradient_64x48", "8515aebae7bf5d750414ad3507d5ad75781052b55815a2b57de0514a5960fbca", 33, 10 },
        { "gradient_320x200", "6b55020053550200aaaafeffa9aafe00ab7dfe0054aa01fff4aa01ffbcaa81ff", 5, 10 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_reference_implementation(string name, string hex, int quality, int tolerance)
    {
        var (hash, q) = Pdq.Compute(Picture(name));

        Assert.Equal(quality, q);
        var distance = hash.Distance(PdqHash.FromHex(hex)!);
        Assert.True(distance <= tolerance, $"{name}: {hash.Hex} is {distance} bits from the reference {hex}.");
    }

    [Fact]
    public void A_checkerboard_has_full_quality()
    {
        Assert.Equal(100, Pdq.Compute(Picture("checker_128x128_s16")).Quality);
    }

    [Fact]
    public void A_resized_copy_still_matches()
    {
        var original = Picture("noise_250x180_seed42");
        var smaller = FrameOps.Resize(original, 200, 144);

        var distance = Pdq.Compute(original).Hash.Distance(Pdq.Compute(smaller).Hash);

        Assert.True(distance <= 31, $"{distance} bits apart.");
    }

    [Fact]
    public void Different_pictures_are_far_apart()
    {
        var a = Pdq.Compute(Picture("noise_97x61_seed7")).Hash;
        var b = Pdq.Compute(Picture("noise_250x180_seed42")).Hash;

        Assert.True(a.Distance(b) > 64);
    }

    [Fact]
    public void Hex_round_trips_and_rejects_rubbish()
    {
        const string hex = "6b343d576191f39cad39f81ee305f41233315078039d33e5503eaf38d707e651";

        Assert.Equal(hex, PdqHash.FromHex(hex.ToUpperInvariant())!.Hex);
        Assert.Null(PdqHash.FromHex("abc"));
        Assert.Null(PdqHash.FromHex(new string('g', 64)));
    }

    [Fact]
    public void Bit_k_is_worth_two_to_the_k_in_hex()
    {
        var bits = new bool[256];
        bits[0] = true;
        bits[255] = true;

        Assert.Equal("8" + new string('0', 62) + "1", PdqHash.FromBits(bits).Hex);
    }

    /// <summary>The reference script's test pictures.</summary>
    internal static RgbFrame Picture(string name)
    {
        var parts = name.Split('_');
        var size = parts[1].Split('x').Select(int.Parse).ToArray();
        var (w, h) = (size[0], size[1]);
        var frame = new RgbFrame(w, h);
        var p = frame.Pixels;
        switch (parts[0])
        {
            case "gradient":
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var i = ((y * w) + x) * 3;
                        p[i] = (byte)(x * 255 / (w - 1));
                        p[i + 1] = (byte)(y * 255 / (h - 1));
                        p[i + 2] = (byte)((x + y) * 255 / (w + h - 2));
                    }
                }

                break;
            case "checker":
                var s = int.Parse(parts[2][1..]);
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var v = (byte)((((x / s) + (y / s)) % 2) == 0 ? 255 : 0);
                        var i = ((y * w) + x) * 3;
                        p[i] = p[i + 1] = p[i + 2] = v;
                    }
                }

                break;
            default:
                long state = int.Parse(parts[2]["seed".Length..]);
                for (var i = 0; i < w * h * 3; i++)
                {
                    state = ((1103515245 * state) + 12345) % 2147483648;
                    p[i] = (byte)((state >> 16) & 255);
                }

                break;
        }

        return frame;
    }
}
