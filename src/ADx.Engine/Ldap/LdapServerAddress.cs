using System.Globalization;

namespace ADx.Engine.Ldap;

/// <summary>
/// Parses the "host:port" server spelling (RSAT-documented, honoured by both wldap32 and
/// OpenLDAP) into its components. The embedded port is not cosmetic: the native stacks let it
/// OVERRIDE the separately-configured port number, so a value like "dc01:3268" passed through
/// unparsed binds the Global Catalog while everything keyed on the configured port -- the GC
/// result-shape safeguards, the TLS scheme, diagnostics -- still believes it is a domain bind.
/// One parser, consulted by both the cmdlet layer (parameter resolution) and the client
/// (connection URI), so the two can never disagree about which port a server string names.
/// </summary>
public static class LdapServerAddress
{
    /// <summary>
    /// Split an optional trailing ":port" off <paramref name="server"/>. IPv6 literals are
    /// respected: a bare "::1" is a host with no port (multiple colons, no brackets), and
    /// "[::1]:636" is the bracketed form. The host comes back verbatim, brackets included.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The text after the port separator is not a number in 1-65535, the host part is empty,
    /// or a bracketed literal is unterminated. Loud by design: a malformed server string
    /// previously travelled into the native stack and failed as an unrelated-looking
    /// connection error.
    /// </exception>
    public static (string? Host, int? Port) Parse(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return (null, null);
        var s = server.Trim();

        // Interior whitespace never appears in a legitimate host name or address, and a value
        // carrying it used to travel into the native stack and fail as an unrelated-looking
        // connection error -- the exact silent-malformation this parser exists to stop.
        if (s.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                $"Server '{server}' contains whitespace inside the value; a host name or address cannot.");

        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0)
                throw new ArgumentException(
                    $"Server '{server}' has an unterminated '[': the bracketed IPv6 form is '[::1]' or '[::1]:636'.");
            if (close == 1)
                throw new ArgumentException($"Server '{server}' has an empty '[]' address.");

            var rest = s[(close + 1)..];
            if (rest.Length == 0) return (s, null);
            if (rest[0] == ':') return (s[..(close + 1)], ParsePort(rest[1..], server));

            throw new ArgumentException(
                $"Server '{server}' has trailing content after the bracketed address; expected nothing or ':port'.");
        }

        var first = s.IndexOf(':');
        if (first < 0) return (s, null);

        // Two or more colons without brackets: a bare IPv6 literal. It has no port form --
        // "::1:636" is itself a valid address -- so nothing is split off.
        if (s.IndexOf(':', first + 1) >= 0) return (s, null);

        var host = s[..first];
        if (host.Length == 0)
            throw new ArgumentException($"Server '{server}' has an empty host before ':'.");

        return (host, ParsePort(s[(first + 1)..], server));
    }

    private static int ParsePort(string text, string original)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
            return port;

        throw new ArgumentException(
            $"The port in server '{original}' must be a number between 1 and 65535 (got '{text}').");
    }
}
