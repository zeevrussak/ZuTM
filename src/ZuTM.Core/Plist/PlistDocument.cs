// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.Core.Plist;

public enum PlistFormat
{
    /// <summary>Sniff the format on read.</summary>
    Auto,

    /// <summary>Apple XML plist (what UTM's config.plist uses).</summary>
    Xml,

    /// <summary>Apple binary bplist00.</summary>
    Binary,
}

/// <summary>Entry point for parsing and serializing property lists.</summary>
public static class PlistDocument
{
    /// <summary>Parses plist bytes, sniffing XML vs binary format.</summary>
    public static PlistNode Parse(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith("bplist00"u8)
            ? PlistBinary.Parse(bytes)
            : PlistXml.Parse(bytes);
    }

    public static PlistNode ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    /// <summary>Parses and asserts the root is a dictionary (the shape of config.plist).</summary>
    public static PlistDictionary ParseDictionary(ReadOnlySpan<byte> bytes)
    {
        return Parse(bytes) as PlistDictionary
            ?? throw new FormatException("Plist root is not a dictionary.");
    }

    public static byte[] Write(PlistNode root, PlistFormat format)
    {
        return format switch
        {
            PlistFormat.Binary => PlistBinary.Write(root),
            PlistFormat.Xml or PlistFormat.Auto => PlistXml.Write(root),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static void WriteFile(string path, PlistNode root, PlistFormat format = PlistFormat.Xml)
    {
        File.WriteAllBytes(path, Write(root, format));
    }

    internal static string ToDebugString(PlistNode node) => PlistFactory.ToDebugString(node);
}
