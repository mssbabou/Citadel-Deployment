namespace CitadelServer;

public sealed class ServerConfig
{
    public string Token { get; init; } = "";
    public int Port { get; init; } = 9090;

    /// <summary>
    /// Loads config from a key=value file (# comments supported).
    /// Returns an empty config (Token = "") if the file does not exist.
    /// </summary>
    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ServerConfig();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 1) continue;
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();
            values[key] = val;
        }

        return new ServerConfig
        {
            Token = values.GetValueOrDefault("token", ""),
            Port = int.TryParse(values.GetValueOrDefault("port", "9090"), out var p) ? p : 9090,
        };
    }
}
