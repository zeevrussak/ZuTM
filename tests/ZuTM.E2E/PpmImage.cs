// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.E2E;

/// <summary>
/// Minimal P6 (binary) PPM reader for QMP `screendump` output — enough to
/// measure whether a guest desktop is rendering.
/// </summary>
public sealed record PpmImage(int Width, int Height, byte[] Rgb)
{
    public static PpmImage Parse(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var magic = ReadToken(reader);
        if (magic != "P6")
        {
            throw new FormatException($"not a binary PPM: magic {magic}");
        }

        var width = int.Parse(ReadToken(reader));
        var height = int.Parse(ReadToken(reader));
        var maxVal = int.Parse(ReadToken(reader));
        if (maxVal is < 1 or > 255)
        {
            throw new FormatException($"unsupported maxval {maxVal}");
        }

        // ReadToken consumes the whitespace that terminates each token —
        // including the single separator after maxval — so the raster starts
        // here immediately.
        var rgb = reader.ReadBytes(width * height * 3);
        if (rgb.Length != width * height * 3)
        {
            throw new EndOfStreamException("truncated PPM raster");
        }

        return new PpmImage(width, height, rgb);
    }

    private static string ReadToken(BinaryReader reader)
    {
        var token = "";
        while (true)
        {
            var b = reader.ReadByte();
            if (b == (byte)'#')
            {
                while (reader.ReadByte() is not ((byte)'\n' or (byte)'\r'))
                {
                }
            }
            else if (char.IsWhiteSpace((char)b))
            {
                if (token.Length > 0)
                {
                    return token;
                }
            }
            else
            {
                token += (char)b;
            }
        }
    }

    /// <summary>Average luma (0–255) across the frame.</summary>
    public double MeanBrightness()
    {
        long total = 0;
        for (var i = 0; i < Rgb.Length; i += 3)
        {
            total += (Rgb[i] * 299 + Rgb[i + 1] * 587 + Rgb[i + 2] * 114) / 1000;
        }

        return total / (double)(Rgb.Length / 3);
    }

    /// <summary>Count of pixels brighter than the threshold — a desktop has many.</summary>
    public long CountBrightPixels(int threshold = 24)
    {
        long count = 0;
        for (var i = 0; i < Rgb.Length; i += 3)
        {
            var luma = (Rgb[i] * 299 + Rgb[i + 1] * 587 + Rgb[i + 2] * 114) / 1000;
            if (luma > threshold)
            {
                count++;
            }
        }

        return count;
    }
}
