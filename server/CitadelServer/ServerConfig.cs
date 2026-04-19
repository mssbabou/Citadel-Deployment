using Tomlyn;
using Tomlyn.Model;

namespace CitadelServer;

public sealed class ServerConfig
{
    public string Token { get; init; } = "";
    public int Port { get; init; } = 9090;
    public int MaxUploadMb { get; init; } = 500;
    public int KeepBackups { get; init; } = 3;
    public string AuditLogPath { get; init; } = "";
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
        var maxUploadMb = 500;
        var keepBackups = 3;
        var auditLogPath = "";

        if (root != null && root.TryGetValue("server", out var serverObj) && serverObj is TomlTable serverTable)
        {
            if (serverTable.TryGetValue("token", out var t))
                token = t.ToString() ?? "";
            if (serverTable.TryGetValue("port", out var p) && p is long pl)
                port = (int)pl;
            if (serverTable.TryGetValue("max_upload_mb", out var mu) && mu is long mul)
                maxUploadMb = (int)mul;
            if (serverTable.TryGetValue("keep_backups", out var kb) && kb is long kbl)
                keepBackups = (int)kbl;
            if (serverTable.TryGetValue("audit_log_path", out var al))
                auditLogPath = al.ToString() ?? "";
        }

        var profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        if (root!.TryGetValue("profiles", out var profilesObj) && profilesObj is TomlTable profilesTable)
        {
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
        }

        return new ServerConfig
        {
            Token = token,
            Port = port,
            MaxUploadMb = maxUploadMb,
            KeepBackups = keepBackups,
            AuditLogPath = auditLogPath,
            Profiles = profiles,
        };
    }

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(Token))
            errors.Add("server.token is empty");
        if (Port < 1 || Port > 65535)
            errors.Add($"server.port out of range: {Port} (must be 1-65535)");
        if (MaxUploadMb < 1 || MaxUploadMb > 10_000)
            errors.Add($"server.max_upload_mb out of range: {MaxUploadMb} (must be 1-10000)");
        if (KeepBackups < 0 || KeepBackups > 100)
            errors.Add($"server.keep_backups out of range: {KeepBackups} (must be 0-100)");

        foreach (var (name, p) in Profiles)
        {
            if (string.IsNullOrEmpty(p.DeployDir))
                errors.Add($"profile '{name}': deploy_dir is empty");
            else if (!Path.IsPathFullyQualified(p.DeployDir))
                errors.Add($"profile '{name}': deploy_dir must be an absolute path (got '{p.DeployDir}')");
        }

        return errors;
    }
}
