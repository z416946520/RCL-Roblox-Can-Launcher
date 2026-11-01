namespace RCL.Core;

/// <summary>
/// Strict parser for Roblox launch links, modelled on Voidstrap's RobloxDeeplink. Anything that
/// reaches RCL from outside (protocol handler, shortcut, pasted link) is reduced to a single
/// allowlisted roblox: URI; the original string is never forwarded to the client, so stray
/// arguments cannot ride along with a link and no other scheme can be dispatched.
/// </summary>
public static class RobloxDeeplink
{
    private const int MaxLength = 32768;

    private static readonly string[] Schemes =
    {
        "roblox-player:", "roblox-studio-auth:", "roblox-studio:", "roblox:"
    };

    private static readonly string[] AllowedSchemes =
    {
        "roblox", "roblox-player", "roblox-studio", "roblox-studio-auth"
    };

    public static bool TryExtract(string? value, out Uri? deeplink)
    {
        deeplink = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Length > MaxLength || value.Any(char.IsControl))
            return false;

        int index = -1;
        foreach (var scheme in Schemes)
        {
            var found = value.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (index < 0 || found < index))
                index = found;
        }

        if (index < 0)
            return TryFromWebsite(value, out deeplink);

        var candidate = TrimToLink(value[index..]).Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("roblox://placeId=", StringComparison.OrdinalIgnoreCase))
            candidate = "roblox://experiences/start?" + candidate["roblox://".Length..];

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;
        if (!AllowedSchemes.Any(s => string.Equals(parsed.Scheme, s, StringComparison.OrdinalIgnoreCase)))
            return false;

        deeplink = parsed;
        return true;
    }

    /// <summary>
    /// RCL only drives RobloxPlayerBeta, so Studio links are refused instead of being handed to
    /// the player executable.
    /// </summary>
    public static bool IsPlayerLink(Uri deeplink) =>
        !deeplink.Scheme.StartsWith("roblox-studio", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the value of a query parameter from a validated player link.</summary>
    public static string? QueryValue(Uri deeplink, string key)
    {
        if (!deeplink.IsAbsoluteUri || string.IsNullOrEmpty(deeplink.Query))
            return null;
        var query = deeplink.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0)
                continue;
            if (!pair[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = Uri.UnescapeDataString(pair[(split + 1)..]).Trim();
            if (value.Length > 0)
                return value;
        }
        return null;
    }

    /// <summary>
    /// Builds the canonical player link. Voidstrap rewrites the legacy roblox://placeId= form to
    /// this one, and the client treats it the same while the job id stays escaped.
    /// </summary>
    public static string BuildPlayerLink(long placeId, string? jobId)
    {
        var link = $"roblox://experiences/start?placeId={placeId}";
        var job = jobId?.Trim();
        if (!string.IsNullOrEmpty(job))
            link += "&gameInstanceId=" + Uri.EscapeDataString(job);
        return link;
    }

    /// <summary>
    /// Cuts the link off at a quote, or at the first whitespace that starts another switch, so
    /// "roblox://... -app" keeps only the link instead of forwarding the trailing switch.
    /// </summary>
    private static string TrimToLink(string value)
    {
        var quote = value.IndexOfAny(new[] { '"', '\'' });
        if (quote >= 0)
            value = value[..quote];

        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
                continue;
            var next = i;
            while (next < value.Length && char.IsWhiteSpace(value[next]))
                next++;
            if (next < value.Length && value[next] == '-')
                return value[..i].Trim();
        }
        return value.Trim();
    }

    private static bool TryFromWebsite(string value, out Uri? deeplink)
    {
        deeplink = null;
        var candidate = TrimToLink(value.Trim().Trim('"', '\''));
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var site) ||
            site.Scheme != Uri.UriSchemeHttps ||
            (!site.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) &&
             !site.Host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (site.AbsolutePath.Equals("/games/start", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate("roblox://experiences/start" + site.Query, UriKind.Absolute, out deeplink);

        var segments = site.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0].Equals("games", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(segments[1], out var placeId) && placeId > 0)
            return Uri.TryCreate($"roblox://experiences/start?placeId={placeId}", UriKind.Absolute, out deeplink);

        return false;
    }
}
