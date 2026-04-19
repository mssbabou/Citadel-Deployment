using CitadelServer;
using Xunit;

namespace CitadelServer.Tests;

public class ServerConfigTests : IDisposable
{
    readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"citadel-cfg-{Guid.NewGuid():N}.toml");

    void Write(string toml) => File.WriteAllText(_tempFile, toml);

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var cfg = ServerConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal("", cfg.Token);
        Assert.Equal(9090, cfg.Port);
        Assert.Equal(500, cfg.MaxUploadMb);
        Assert.Equal(3, cfg.KeepBackups);
        Assert.Empty(cfg.Profiles);
    }

    [Fact]
    public void MalformedToml_Throws()
    {
        Write("this is = = not toml [[[");
        Assert.ThrowsAny<Exception>(() => ServerConfig.Load(_tempFile));
    }

    [Fact]
    public void ValidConfig_LoadsAllFields()
    {
        Write("""
            [server]
            token = "abc"
            port = 1234
            max_upload_mb = 100
            keep_backups = 5
            audit_log_path = "/var/log/citadel.jsonl"

            [profiles.myapp]
            deploy_dir = "/opt/myapp"
            services = ["a.service", "b.service"]
            post_update_command = "echo hi"
            """);

        var cfg = ServerConfig.Load(_tempFile);
        Assert.Equal("abc", cfg.Token);
        Assert.Equal(1234, cfg.Port);
        Assert.Equal(100, cfg.MaxUploadMb);
        Assert.Equal(5, cfg.KeepBackups);
        Assert.Equal("/var/log/citadel.jsonl", cfg.AuditLogPath);

        Assert.True(cfg.Profiles.TryGetValue("myapp", out var p));
        Assert.Equal("/opt/myapp", p!.DeployDir);
        Assert.Equal(new[] { "a.service", "b.service" }, p.Services);
        Assert.Equal("echo hi", p.PostUpdateCommand);
    }

    [Fact]
    public void MissingServerSection_UsesDefaults()
    {
        Write("""
            [profiles.foo]
            deploy_dir = "/opt/foo"
            services = []
            """);

        var cfg = ServerConfig.Load(_tempFile);
        Assert.Equal("", cfg.Token);
        Assert.Equal(9090, cfg.Port);
        Assert.Single(cfg.Profiles);
    }

    [Fact]
    public void EmptyProfilesTable_Ok()
    {
        Write("""
            [server]
            token = "x"
            port = 9090
            """);

        var cfg = ServerConfig.Load(_tempFile);
        Assert.Empty(cfg.Profiles);
    }

    [Fact]
    public void ProfileLookup_CaseInsensitive()
    {
        Write("""
            [server]
            token = "x"
            port = 9090

            [profiles.MyApp]
            deploy_dir = "/opt/myapp"
            services = []
            """);

        var cfg = ServerConfig.Load(_tempFile);
        Assert.True(cfg.Profiles.ContainsKey("myapp"));
        Assert.True(cfg.Profiles.ContainsKey("MYAPP"));
    }

    [Fact]
    public void NonStringServiceEntries_AreFilteredOut()
    {
        Write("""
            [server]
            token = "x"
            port = 9090

            [profiles.foo]
            deploy_dir = "/opt/foo"
            services = ["ok.service", 42, true, "also-ok.service"]
            """);

        var cfg = ServerConfig.Load(_tempFile);
        var p = cfg.Profiles["foo"];
        Assert.Equal(new[] { "ok.service", "also-ok.service" }, p.Services);
    }

    [Fact]
    public void Validate_EmptyToken_ReportsError()
    {
        var cfg = new ServerConfig { Token = "", Port = 9090 };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("token"));
    }

    [Fact]
    public void Validate_PortOutOfRange_ReportsError()
    {
        var cfg = new ServerConfig { Token = "x", Port = 70_000 };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("port"));
    }

    [Fact]
    public void Validate_MaxUploadOutOfRange_ReportsError()
    {
        var cfg = new ServerConfig { Token = "x", Port = 9090, MaxUploadMb = 0 };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("max_upload_mb"));
    }

    [Fact]
    public void Validate_KeepBackupsNegative_ReportsError()
    {
        var cfg = new ServerConfig { Token = "x", Port = 9090, KeepBackups = -1 };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("keep_backups"));
    }

    [Fact]
    public void Validate_RelativeDeployDir_ReportsError()
    {
        var cfg = new ServerConfig
        {
            Token = "x",
            Port = 9090,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                ["foo"] = new() { DeployDir = "relative/path", Services = [] },
            },
        };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("deploy_dir") && e.Contains("absolute"));
    }

    [Fact]
    public void Validate_EmptyDeployDir_ReportsError()
    {
        var cfg = new ServerConfig
        {
            Token = "x",
            Port = 9090,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                ["foo"] = new() { DeployDir = "", Services = [] },
            },
        };
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("deploy_dir") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_GoodConfig_ReturnsEmpty()
    {
        var cfg = new ServerConfig
        {
            Token = "x",
            Port = 9090,
            MaxUploadMb = 100,
            KeepBackups = 3,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                ["foo"] = new() { DeployDir = "/opt/foo", Services = [] },
            },
        };
        Assert.Empty(cfg.Validate());
    }
}
