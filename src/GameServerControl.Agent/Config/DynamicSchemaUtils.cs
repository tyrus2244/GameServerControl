using System.Text;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Helpers shared by dynamic schema providers: type inference from default values
/// and key humanization (FooBarRate → "Foo bar rate").
/// </summary>
internal static class DynamicSchemaUtils
{
    public static ConfigField InferField(string key, string? rawDefault, string source)
    {
        var v = (rawDefault ?? "").Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v.Substring(1, v.Length - 2);

        var isBool = v.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                     v.Equals("False", StringComparison.OrdinalIgnoreCase);
        var isPasswordKey = key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("Token", StringComparison.OrdinalIgnoreCase);
        var isInt = int.TryParse(v, out _);
        var isDec = !isInt && double.TryParse(v, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out _);

        var type = isBool       ? ConfigFieldType.Toggle
                 : isPasswordKey ? ConfigFieldType.Password
                 : isInt         ? ConfigFieldType.Integer
                 : isDec         ? ConfigFieldType.Decimal
                 :                 ConfigFieldType.Text;

        var label = Humanize(key);
        var description = $"Auto-discovered from {source}. Default: {(string.IsNullOrEmpty(v) ? "(empty)" : v)}";
        return new ConfigField(key, label, type, description, Default: isBool ? v.ToLowerInvariant() : v);
    }

    /// <summary>
    /// "bEnableFastTravel" → "Enable fast travel".
    /// "WorkSpeedRate" → "Work speed rate".
    /// "mAutoPauseServerOnEmpty" → "Auto pause server on empty".
    /// </summary>
    public static string Humanize(string key)
    {
        // Drop common Unreal-ish leading prefixes
        var stripped = key;
        if (stripped.Length > 1 && stripped[0] == 'b' && char.IsUpper(stripped[1])) stripped = stripped[1..];
        else if (stripped.Length > 1 && stripped[0] == 'm' && char.IsUpper(stripped[1])) stripped = stripped[1..];

        var sb = new StringBuilder(stripped.Length + 8);
        for (int i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(stripped[i - 1])) sb.Append(' ');
            else if (i > 0 && char.IsUpper(c) && i + 1 < stripped.Length && char.IsLower(stripped[i + 1])) sb.Append(' ');
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        if (s.Length == 0) return key;
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }
}
