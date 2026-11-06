using System.Numerics;
using System.Text.RegularExpressions;

namespace WindowsIsland.Plugins;

// C20：目录使用严格 SemVer；不改变应用更新标签的解析规则。
internal sealed partial record CatalogVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string Prerelease)
    : IComparable<CatalogVersion>
{
    public bool IsPrerelease => Prerelease.Length != 0;
    public static bool TryParse(string? text, out CatalogVersion value)
    {
        value = null!;
        if (text is null) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        var pre = match.Groups[4].Value;
        if (pre.Split('.').Any(p => Numeric(p) && p.Length > 1 && p[0] == '0')) return false;
        value = new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value),
            BigInteger.Parse(match.Groups[3].Value), pre);
        return true;
    }
    public static CatalogVersion Parse(string value) => TryParse(value, out var version) ? version
        : throw new PluginException(PluginErrorCode.Version, "插件版本无效。", "C20");
    public int CompareTo(CatalogVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison == 0) comparison = Minor.CompareTo(other.Minor);
        if (comparison == 0) comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var a = Numeric(left[i]); var b = Numeric(right[i]);
            comparison = a && b ? BigInteger.Parse(left[i]).CompareTo(BigInteger.Parse(right[i]))
                : a != b ? a ? -1 : 1 : string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
    private static bool Numeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
