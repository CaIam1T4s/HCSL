using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace HCSL.Core;

public sealed record PlayerServer(Guid Id, string Name, string Host, int Port = 27015, bool HasPassword = false)
{
    public string Endpoint => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    public override string ToString() => $"{Name} — {Endpoint}";
}

public static class PlayerServerRules
{
    public static PlayerServer Normalize(PlayerServer server)
    {
        if (server is null || server.Host is null || server.Name is null)
            throw new ArgumentException("HCSL-CLIENT-ADDRESS: Host and name are required.");
        var host = server.Host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (IPAddress.TryParse(host, out var ip))
        {
            // Reject legacy abbreviated IPv4, integer addresses, and scoped IPv6.
            if (host.Contains('%') || (!host.Contains(':') &&
                (!Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$") || host.Split('.').Any(x => x.Length > 1 && x[0] == '0'))))
                throw new ArgumentException("HCSL-CLIENT-ADDRESS: Use a full IP literal or DNS name.");
            host = ip.ToString();
        }
        else
        {
            try { host = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
            catch (ArgumentException) { throw new ArgumentException("HCSL-CLIENT-ADDRESS: Invalid host name."); }
            if (host.Length > 253 || !host.Split('.').All(label =>
                Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) ||
                host.All(c => char.IsAsciiDigit(c) || c == '.'))
                throw new ArgumentException("HCSL-CLIENT-ADDRESS: Enter an IP address or host name, without scheme, path or port.");
        }
        if (server.Port is < 1 or > 65535) throw new ArgumentException("HCSL-CLIENT-PORT: Port must be 1..65535.");
        var name = server.Name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("HCSL-CLIENT-NAME: Name must contain 1..80 printable characters.");
        return server with { Host = host, Name = name };
    }

    public static void Password(string value)
    {
        if (value.Length > 128 || value.Any(c => char.IsControl(c) || c is '"' or '\\' or ';' or '+'))
            throw new ArgumentException("HCSL-CLIENT-PASSWORD: Password contains unsupported console-command characters or exceeds 128 characters.");
    }

    public static string ConnectCommand(PlayerServer server, string password = "")
    {
        server = Normalize(server);
        Password(password);
        return $"password \"{password}\"; connect {server.Endpoint}";
    }
}
