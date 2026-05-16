using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Ensures the agent never ships with a hardcoded API token.
///
/// On startup, if <c>Agent:ApiToken</c> is empty, a known placeholder, or shorter
/// than a sensible threshold, we generate a cryptographically random 32-byte
/// token, write it back to <c>appsettings.json</c>, and log it to console once.
///
/// This means a fresh clone of the repo can't be hit by anyone with knowledge of
/// the source — they'd need the actual deployed appsettings file. Operators get
/// a one-time chance to see and copy the token from the agent's console output
/// or from the file directly.
/// </summary>
public static class FirstRunTokenGenerator
{
    // Anything matching these is treated as "no real token set yet".
    private static readonly string[] PlaceholderTokens =
    {
        "",
        "REPLACE_WITH_32_CHARS+",
        "CHANGE_ME",
        "your-token-here",
        "<generate>"
    };

    /// <summary>
    /// Returns true if a new token was generated and persisted, false otherwise.
    /// Caller is responsible for updating in-memory configuration.
    /// </summary>
    public static bool EnsureToken(IConfiguration cfg, string baseDir, ILogger logger, out string token)
    {
        var current = cfg["Agent:ApiToken"] ?? "";
        if (!IsPlaceholder(current))
        {
            token = current;
            return false;
        }

        token = GenerateToken();
        var appsettingsPath = Path.Combine(baseDir, "appsettings.json");
        if (!File.Exists(appsettingsPath))
        {
            logger.LogWarning("appsettings.json not found at {Path}; generated token will only persist in memory.", appsettingsPath);
            return true;
        }

        try
        {
            var json = File.ReadAllText(appsettingsPath);
            // Surgical regex replace so we don't reformat the user's whole settings file.
            var updated = Regex.Replace(
                json,
                "\"ApiToken\"\\s*:\\s*\"[^\"]*\"",
                $"\"ApiToken\": \"{token}\"",
                RegexOptions.Singleline);
            if (updated == json)
            {
                logger.LogWarning("Could not find ApiToken field in appsettings.json to update.");
                return true;
            }
            // Atomic write so we don't corrupt the file on a crash mid-write.
            var tmp = appsettingsPath + ".tmp";
            File.WriteAllText(tmp, updated);
            File.Replace(tmp, appsettingsPath, appsettingsPath + ".bak");

            // Log only the first 8 chars to the log file; print the full token to console.
            logger.LogInformation("Generated new agent API token (prefix {Prefix}…). Full token saved to appsettings.json.", token[..Math.Min(8, token.Length)]);
            Console.WriteLine();
            Console.WriteLine("====================================================================");
            Console.WriteLine("  GENERATED NEW AGENT API TOKEN — copy this into your client now:");
            Console.WriteLine();
            Console.WriteLine($"     {token}");
            Console.WriteLine();
            Console.WriteLine("  Saved to: " + appsettingsPath);
            Console.WriteLine("  This message only appears once. Subsequent boots reuse the token.");
            Console.WriteLine("====================================================================");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist generated token to appsettings.json");
        }
        return true;
    }

    private static bool IsPlaceholder(string s)
    {
        if (s.Length < 16) return true; // anything short is suspicious
        foreach (var p in PlaceholderTokens)
            if (string.Equals(s, p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // URL-safe base64 (no /, no +, no =) for friendlier copy/paste.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
