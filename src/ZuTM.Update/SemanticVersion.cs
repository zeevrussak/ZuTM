// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Globalization;

namespace ZuTM.Update;

/// <summary>Strict semantic-version parser (semver.org 2.0.0) for comparing releases.</summary>
public readonly record struct SemanticVersion
{
    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Pre-release identifiers ("rc.1"); absent on stable releases.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>Build metadata — ignored for precedence per the spec.</summary>
    public string? BuildMetadata { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // <core>[-<pre>][+<build>]
        var core = text;
        string? pre = null;
        string? build = null;

        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            build = core[(plus + 1)..];
            core = core[..plus];
        }

        var minus = core.IndexOf('-');
        if (minus >= 0)
        {
            pre = core[(minus + 1)..];
            core = core[..minus];
        }

        var parts = core.Split('.');
        if (parts.Length is < 3 or > 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            // Semver forbids leading zeroes ("01" is invalid).
            if (part.Length > 1 && part[0] == '0')
            {
                return false;
            }
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var identifiers = pre is null ? [] : pre.Split('.');
        if (pre is not null)
        {
            // Empty pre-release identifiers are invalid ("1.0.0-" / "1.0.0-rc..1").
            if (identenciesValid(identifiers) == false)
            {
                return false;
            }
        }

        version = new SemanticVersion(major, minor, patch, identifiers, build);
        return true;

        static bool identenciesValid(string[] identifiers) =>
            identifiers.Length > 0 && identifiers.All(i => i.Length > 0);
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a valid semantic version.");

    /// <summary>Strict precedence comparison per semver §11: build metadata ignored.</summary>
    public int CompareTo(SemanticVersion other)
    {
        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        if (Minor != other.Minor)
        {
            return Minor.CompareTo(other.Minor);
        }

        if (Patch != other.Patch)
        {
            return Patch.CompareTo(other.Patch);
        }

        // A pre-release sorts before the release; more identifiers sort higher
        // when the prefixes match; numeric identifiers compare numerically.
        if (IsPreRelease != other.IsPreRelease)
        {
            return IsPreRelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var mine = PreRelease[i];
            var theirs = other.PreRelease[i];
            var mineNumeric = long.TryParse(mine, NumberStyles.None, CultureInfo.InvariantCulture, out var mineValue);
            var theirsNumeric = long.TryParse(theirs, NumberStyles.None, CultureInfo.InvariantCulture, out var theirsValue);

            if (mineNumeric && theirsNumeric)
            {
                if (mineValue != theirsValue)
                {
                    return mineValue.CompareTo(theirsValue);
                }
            }
            else
            {
                var lexical = string.CompareOrdinal(mine, theirs);
                if (lexical != 0)
                {
                    return lexical;
                }

                if (mineNumeric != theirsNumeric)
                {
                    return mineNumeric ? -1 : 1; // numeric identifiers sort below alphanumeric
                }
            }
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    public override string ToString()
    {
        var version = $"{Major}.{Minor}.{Patch}";
        if (IsPreRelease)
        {
            version += "-" + string.Join(".", PreRelease);
        }

        if (BuildMetadata is not null)
        {
            version += "+" + BuildMetadata;
        }

        return version;
    }
}
