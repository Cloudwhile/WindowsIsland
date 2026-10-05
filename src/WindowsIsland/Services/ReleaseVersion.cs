using System.Reflection;
using System.Text.RegularExpressions;

namespace WindowsIsland.Services;

internal sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string Prerelease, string Text)
    : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease.Length != 0;
    public string Display => Text.Split('+')[0];
    public static ReleaseVersion Current { get; } = FromInformationalVersion(
        typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static ReleaseVersion FromInformationalVersion(string? value) =>
        TryParse(value, out var version) ? version! : new(0, 0, 0, "", "0.0.0");

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value)) return false;
        var text = value.StartsWith('v') ? value[1..] : value;
        var match = VersionPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(part => Numeric(part) && part.Length > 1 && part[0] == '0')) return false;
        version = new(major, minor, patch, prerelease, text);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison == 0) comparison = Minor.CompareTo(other.Minor);
        if (comparison == 0) comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = Numeric(left[index]);
            var rightNumeric = Numeric(right[index]);
            if (leftNumeric && rightNumeric)
            {
                comparison = left[index].Length.CompareTo(right[index].Length);
                if (comparison == 0) comparison = string.CompareOrdinal(left[index], right[index]);
            }
            else comparison = leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool Numeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
