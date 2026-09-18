// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ZuTM.Core.Plist;

/// <summary>XML property list (public Apple format) reader and writer.</summary>
public static class PlistXml
{
    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = true,
        IndentChars = "\t",
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        OmitXmlDeclaration = false,
        NewLineHandling = NewLineHandling.None,
    };

    /// <summary>Parses an XML plist document. The root may be any plist node.</summary>
    public static PlistNode Parse(ReadOnlySpan<byte> utf8Bytes)
    {
        var text = Encoding.UTF8.GetString(utf8Bytes);
        return ParseText(text);
    }

    public static PlistNode ParseText(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Malformed XML plist: {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null || root.Name != "plist")
        {
            throw new FormatException("XML plist must have a <plist> root element.");
        }

        var value = root.Elements().FirstOrDefault()
            ?? throw new FormatException("<plist> element has no value.");

        return ParseElement(value);
    }

    private static PlistNode ParseElement(XElement element) => element.Name.LocalName switch
    {
        "dict" => ParseDict(element),
        "array" => ParseArray(element),
        "string" => new PlistString(element.Value),
        "integer" => ParseInteger(element.Value),
        "real" => ParseReal(element.Value),
        "true" => new PlistBoolean(true),
        "false" => new PlistBoolean(false),
        "data" => new PlistData(DecodeBase64(element.Value)),
        "date" => new PlistDate(ParseDate(element.Value)),
        _ => throw new FormatException($"Unsupported plist element <{element.Name.LocalName}>."),
    };

    private static PlistDictionary ParseDict(XElement element)
    {
        var dict = new PlistDictionary();
        var children = element.Elements().ToList();
        if (children.Count % 2 != 0)
        {
            throw new FormatException("<dict> has an odd number of child elements (unterminated key/value pair).");
        }

        for (var i = 0; i < children.Count; i += 2)
        {
            var keyElement = children[i];
            if (keyElement.Name.LocalName != "key")
            {
                throw new FormatException($"Expected <key> at position {i} in <dict>, found <{keyElement.Name.LocalName}>.");
            }

            dict[keyElement.Value] = ParseElement(children[i + 1]);
        }

        return dict;
    }

    private static PlistArray ParseArray(XElement element)
    {
        var array = new PlistArray();
        foreach (var child in element.Elements())
        {
            array.Add(ParseElement(child));
        }

        return array;
    }

    private static PlistInteger ParseInteger(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"Invalid <integer> value \"{value}\".");
        }

        return new PlistInteger(result);
    }

    private static PlistReal ParseReal(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"Invalid <real> value \"{value}\".");
        }

        return new PlistReal(result);
    }

    private static byte[] DecodeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(RemoveWhitespace(value));
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid <data> base64 payload.", ex);
        }
    }

    private static DateTimeOffset ParseDate(string value)
    {
        // Apple XML plists use ISO-8601 without offset ("2022-01-01T12:00:00Z" or no Z); accept both.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            throw new FormatException($"Invalid <date> value \"{value}\".");
        }

        return result.ToUniversalTime();
    }

    private static string RemoveWhitespace(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Serializes a node as an XML plist document bytes (UTF-8, no BOM, tab-indented — matching UTM's output).</summary>
    public static byte[] Write(PlistNode root)
    {
        using var buffer = new MemoryStream();
        Write(buffer, root);
        return buffer.ToArray();
    }

    public static void Write(Stream stream, PlistNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var writer = XmlWriter.Create(stream, WriterSettings);
        writer.WriteStartDocument();
        writer.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
        writer.WriteStartElement("plist");
        writer.WriteAttributeString("version", "1.0");
        WriteElement(writer, root);
        writer.WriteEndElement();
        writer.Flush();
    }

    private static void WriteElement(XmlWriter writer, PlistNode node)
    {
        switch (node)
        {
            case PlistDictionary dict:
                writer.WriteStartElement("dict");
                foreach (var (key, value) in dict)
                {
                    writer.WriteElementString("key", key);
                    WriteElement(writer, value);
                }

                writer.WriteEndElement();
                break;
            case PlistArray array:
                writer.WriteStartElement("array");
                foreach (var item in array)
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndElement();
                break;
            case PlistString s:
                writer.WriteElementString("string", s.Value);
                break;
            case PlistInteger i:
                writer.WriteElementString("integer", i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PlistReal r:
                writer.WriteElementString("real", r.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case PlistBoolean b:
                writer.WriteElementString(b.Value ? "true" : "false", null);
                break;
            case PlistData d:
                writer.WriteElementString("data", Convert.ToBase64String(d.Value));
                break;
            case PlistDate dt:
                writer.WriteElementString("date", dt.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidOperationException($"Cannot serialize plist node {node.GetType()}.");
        }
    }
}
