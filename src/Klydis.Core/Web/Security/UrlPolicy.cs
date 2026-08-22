using System.Net;

namespace Klydis.Core.Web.Security;

/// <summary>
/// Pure URL/address policy: which schemes the agent may fetch and which addresses are
/// private, reserved, or link-local and therefore never reachable from an autonomous agent.
/// All address classification is deterministic and unit-testable (no I/O).
/// </summary>
public static class UrlPolicy
{
    /// <summary>Schemes an autonomous agent may fetch. Everything else is blocked by policy.</summary>
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttps,
        Uri.UriSchemeHttp
    };

    /// <summary>Explicitly blocked schemes (documentation value; the allowlist is the authority).</summary>
    public static readonly string[] BlockedSchemes =
    {
        "file", "ftp", "data", "javascript", "blob", "chrome", "about", "ws", "wss",
        "gopher", "telnet", "smb", "ldap", "mailto"
    };

    public static bool IsSchemeAllowed(Uri uri) => AllowedSchemes.Contains(uri.Scheme);

    /// <summary>
    /// True when the address must never be contacted by the agent: loopback, private ranges
    /// (10/8, 172.16/12, 192.168/16), link-local (169.254/16, fe80::/10), CGNAT (100.64/10),
    /// benchmarking (198.18/15), multicast, reserved, unspecified, and IPv4-mapped IPv6 forms.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            byte b0 = bytes[0];
            byte b1 = bytes[1];
            byte b2 = bytes[2];

            // 0.0.0.0/8 — "this network" (includes the unspecified address).
            if (b0 == 0) return true;
            // 10.0.0.0/8
            if (b0 == 10) return true;
            // 100.64.0.0/10 — CGNAT
            if (b0 == 100 && b1 is >= 64 and <= 127) return true;
            // 127.0.0.0/8 — loopback
            if (b0 == 127) return true;
            // 169.254.0.0/16 — link-local
            if (b0 == 169 && b1 == 254) return true;
            // 172.16.0.0/12
            if (b0 == 172 && b1 is >= 16 and <= 31) return true;
            // 192.0.0.0/24 — IETF protocol assignments (incl. 192.0.0.9/10 benchmarking)
            if (b0 == 192 && b1 == 0 && b2 == 0) return true;
            // 192.168.0.0/16
            if (b0 == 192 && b1 == 168) return true;
            // 198.18.0.0/15 — benchmarking
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return true;
            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
            if (b0 >= 224) return true;
            return false;
        }

        // IPv6
        if (address.IsIPv6LinkLocal) return true;   // fe80::/10
        if (address.IsIPv6Multicast) return true;   // ff00::/8
        if (address.IsIPv6SiteLocal) return true;   // fec0::/10 (deprecated)
        if (bytes.All(b => b == 0)) return true;    // ::
        // fc00::/7 — unique local
        if ((bytes[0] & 0xFE) == 0xFC) return true;
        return false;
    }
}
