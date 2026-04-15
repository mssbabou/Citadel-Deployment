using Tomlyn;
using Tomlyn.Model;

namespace CitadelServer;

public sealed class ServerConfig
{
    public string Token { get; init; } = "";
    public int Port { get; init; } = 9090;
    public IReadOnlyDictionary<string, Profile> Profiles { get; init; } = new Dictionary<string, Profile>();

    public sealed class Profile
    {
        public string DeployDir { get; init; } = "";
        public string[] Services { get; init; } = [];
        public string PostUpdateCommand { get; init; } = "";
    }

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ServerConfig();

        var root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path));

        var token = "";
        var port = 9090;

        if (root != null && root.TryGetValue("server", out var serverObj) && serverObj is TomlTable serverTable)
        {
            if (serverTable.TryGetValue("token", out var t))
                token = t.ToString() ?? "";
            if (serverTable.TryGetValue("port", out var p) && p is long pl)
                port = (int)pl;
        }

        var profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        if (!root!.TryGetValue("profiles", out var profilesObj) || profilesObj is not TomlTable profilesTable)
            return new ServerConfig { Token = token, Port = port, Profiles = profiles };
        foreach (var (name, value) in profilesTable)
        {
            if (value is not TomlTable profileTable) continue;

            var deployDir = profileTable.TryGetValue("deploy_dir", out var dd) ? dd.ToString() ?? "" : "";
            var services = profileTable.TryGetValue("services", out var svcObj) && svcObj is TomlArray svcArr
                ? svcArr.OfType<string>().ToArray()
                : [];
            var postUpdateCommand = profileTable.TryGetValue("post_update_command", out var puc) ? puc.ToString() ?? "" : "";

            profiles[name] = new Profile { DeployDir = deployDir, Services = services, PostUpdateCommand = postUpdateCommand };
        }

        return new ServerConfig { Token = token, Port = port, Profiles = profiles };
    }
}
