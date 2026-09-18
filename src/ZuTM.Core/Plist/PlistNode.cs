// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Clean-room implementation of the Apple property list model used by the
// UTM bundle format. No code derived from UTM or Apple sources.

using System.Collections;
using System.Text;

namespace ZuTM.Core.Plist;

/// <summary>
/// A property-list value node. Property lists form a tree of dictionaries,
/// arrays, and scalars — see <see href="https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/PropertyLists"/> (public specification).
/// </summary>
public abstract record PlistNode
{
    public override string ToString() => PlistDocument.ToDebugString(this);
}

/// <summary>Ordered string-keyed dictionary. Key order is preserved on serialization.</summary>
public sealed record PlistDictionary : PlistNode, IEnumerable<KeyValuePair<string, PlistNode>>
{
    private readonly OrderedDictionary<string, PlistNode> _items = [];

    public int Count => _items.Count;
    public ICollection<string> Keys => _items.Keys;
    public ICollection<PlistNode> Values => _items.Values;

    /// <summary>Gets or adds/overwrites a value. Setting a <c>null</c> node removes the key.</summary>
    public PlistNode? this[string key]
    {
        get => _items.GetValueOrDefault(key);
        set
        {
            if (value is null)
            {
                _items.Remove(key);
            }
            else
            {
                _items[key] = value;
            }
        }
    }

    public void Add(string key, PlistNode value) => _items.Add(key, value);

    public bool Remove(string key) => _items.Remove(key);

    public bool ContainsKey(string key) => _items.ContainsKey(key);

    public bool TryGetValue(string key, out PlistNode value) => _items.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, PlistNode>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();

    // -- Typed convenience accessors (missing or wrong-typed key ⇒ default) --

    public string? GetString(string key) => this[key] is PlistString s ? s.Value : null;

    public string GetString(string key, string fallback) => GetString(key) ?? fallback;

    public PlistInteger? GetInteger(string key) => this[key] as PlistInteger;

    public long GetInteger(string key, long fallback) => GetInteger(key)?.Value ?? fallback;

    public PlistBoolean? GetBoolean(string key) => this[key] as PlistBoolean;

    public bool GetBoolean(string key, bool fallback) => GetBoolean(key)?.Value ?? fallback;

    public PlistReal? GetReal(string key) => this[key] as PlistReal;

    public double GetReal(string key, double fallback) => GetReal(key)?.Value ?? fallback;

    public PlistArray? GetArray(string key) => this[key] as PlistArray;

    public PlistDictionary? GetDictionary(string key) => this[key] as PlistDictionary;

    public PlistData? GetData(string key) => this[key] as PlistData;

    public PlistDate? GetDate(string key) => this[key] as PlistDate;
}

/// <summary>Ordered collection of values.</summary>
public sealed record PlistArray : PlistNode, IEnumerable<PlistNode>
{
    private readonly List<PlistNode> _items = [];

    public int Count => _items.Count;

    public PlistNode this[int index] => _items[index];

    public void Add(PlistNode value) => _items.Add(value);

    public IEnumerator<PlistNode> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();

    public IReadOnlyList<PlistNode> AsReadOnly() => _items.AsReadOnly();
}

public sealed record PlistString(string Value) : PlistNode;

public sealed record PlistInteger(long Value) : PlistNode;

public sealed record PlistReal(double Value) : PlistNode;

public sealed record PlistBoolean(bool Value) : PlistNode;

/// <summary>Raw bytes. Value equality is over the byte contents.</summary>
public sealed record PlistData : PlistNode
{
    public byte[] Value { get; }

    public PlistData(byte[] value) => Value = value;

    public bool Equals(PlistData? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

/// <summary>Date-time in UTC. Plist dates carry no time-zone designator (implicitly UTC per the spec).</summary>
public sealed record PlistDate(DateTimeOffset Value) : PlistNode
{
    /// <summary>Apple epoch: 2001-01-01T00:00:00Z.</summary>
    public static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

public static class PlistFactory
{
    /// <summary>Builds a dictionary from alternating key/value pairs.</summary>
    public static PlistDictionary Dict(params (string Key, PlistNode Value)[] entries)
    {
        var dict = new PlistDictionary();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }
        return dict;
    }

    public static PlistArray Array(params PlistNode[] items)
    {
        var array = new PlistArray();
        foreach (var item in items)
        {
            array.Add(item);
        }
        return array;
    }

    public static PlistArray Strings(params string[] items)
    {
        var array = new PlistArray();
        foreach (var item in items)
        {
            array.Add(new PlistString(item));
        }
        return array;
    }

    public static string ToDebugString(PlistNode node) => node switch
    {
        PlistDictionary dict => $"{{{string.Join(", ", dict.Select(kv => $"{kv.Key}: {ToDebugString(kv.Value)}"))}}}",
        PlistArray array => $"[{string.Join(", ", array.Select(ToDebugString))}]",
        PlistString s => $"\"{s.Value}\"",
        PlistInteger i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PlistReal r => r.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        PlistBoolean b => b.Value ? "true" : "false",
        PlistData d => $"data[{d.Value.Length}]",
        PlistDate dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Unknown plist node {node.GetType()}"),
    };

    /// <summary>Renders a node as a human-readable indented string (diagnostics and golden tests).</summary>
    public static string ToPrettyString(PlistNode node, string indent = "")
    {
        var sb = new StringBuilder();
        AppendNode(sb, node, indent);
        return sb.ToString();

        static void AppendNode(StringBuilder sb, PlistNode node, string indent)
        {
            switch (node)
            {
                case PlistDictionary dict:
                    sb.AppendLine("{");
                    var inner = indent + "  ";
                    foreach (var (key, value) in dict)
                    {
                        sb.Append(inner).Append(key).Append(" = ");
                        AppendNode(sb, value, inner);
                    }
                    sb.Append(indent).AppendLine("}");
                    break;
                case PlistArray array:
                    sb.AppendLine("(");
                    foreach (var item in array)
                    {
                        sb.Append(indent + "  ");
                        AppendNode(sb, item, indent + "  ");
                    }
                    sb.Append(indent).AppendLine(")");
                    break;
                default:
                    sb.AppendLine(ToDebugString(node));
                    break;
            }
        }
    }
}
