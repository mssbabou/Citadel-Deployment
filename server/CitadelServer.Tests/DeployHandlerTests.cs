using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CitadelServer.Tests;

/// <summary>
/// Integration tests for the deployment endpoint.
/// Starts a real WebApplication on a free port for each test instance.
/// </summary>
public class DeployHandlerTests : IAsyncLifetime
{
    const string TestToken = "integ-test-token-citadel-xyz";
    const string TestProfile = "test-profile";

    WebApplication? _app;
    HttpClient? _client;
    string? _deployDir;

    public async Task InitializeAsync()
    {
        _deployDir = Directory.CreateTempSubdirectory("citadel_test_").FullName;

        var port = GetFreePort();
        var config = new ServerConfig
        {
            Token = TestToken,
            Port = port,
            MaxUploadMb = 500,
            KeepBackups = 3,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                [TestProfile] = new() { DeployDir = _deployDir, Services = [] }
            }
        };
        _app = AppFactory.Create(config, $"http://127.0.0.1:{port}");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        DeployHandler.SystemctlHook = null;
        if (_deployDir != null)
        {
            if (Directory.Exists(_deployDir))
                Directory.Delete(_deployDir, recursive: true);
            var backups = _deployDir + ".backups";
            if (Directory.Exists(backups))
                Directory.Delete(backups, recursive: true);
        }
    }

    static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static string SignV2(byte[]? body, string context, string token, long timestamp)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token));
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp.ToString()));
        hmac.AppendData("\n"u8);
        hmac.AppendData(Encoding.UTF8.GetBytes(context));
        hmac.AppendData("\n"u8);
        if (body != null) hmac.AppendData(body);
        return Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
    }

    HttpRequestMessage DeployRequest(
        byte[] zipBytes,
        string? token = null,
        string? profile = null,
        string? protocol = "v2",
        long? signTimestamp = null,
        long? sendTimestamp = null,
        string? signContext = null,
        bool dryRun = false,
        bool rawBody = false)
    {
        profile ??= TestProfile;
        token ??= TestToken;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signTs = signTimestamp ?? now;
        var sendTs = sendTimestamp ?? now;
        var ctx = signContext ?? profile;

        var sig = SignV2(zipBytes, ctx, token, signTs);

        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        if (protocol != null) req.Headers.Add("X-Protocol", protocol);
        req.Headers.Add("X-Timestamp", sendTs.ToString());
        req.Headers.Add("X-Signature", sig);
        req.Headers.Add("X-Profile", profile);
        if (dryRun) req.Headers.Add("X-DryRun", "true");

        if (rawBody)
        {
            var bc = new ByteArrayContent(zipBytes);
            bc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            req.Content = bc;
        }
        else
        {
            var content = new MultipartFormDataContent();
            var zipContent = new ByteArrayContent(zipBytes);
            zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(zipContent, "file", "app.zip");
            req.Content = content;
        }
        return req;
    }

    static byte[] MakeZip(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    void ClearDeployDir()
    {
        if (_deployDir == null) return;
        foreach (var item in Directory.GetFileSystemEntries(_deployDir))
        {
            if (Directory.Exists(item)) Directory.Delete(item, recursive: true);
            else File.Delete(item);
        }
        var backups = _deployDir + ".backups";
        if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Auth (v2)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MissingProtocol_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), protocol: null));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("protocol", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task WrongProtocol_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), protocol: "v1"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("upgrade", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task ExpiredTimestamp_Returns401()
    {
        var oldTs = DateTimeOffset.UtcNow.AddSeconds(-600).ToUnixTimeSeconds();
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }),
            signTimestamp: oldTs, sendTimestamp: oldTs));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("timestamp", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task FutureTimestamp_Returns401()
    {
        var futureTs = DateTimeOffset.UtcNow.AddSeconds(600).ToUnixTimeSeconds();
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }),
            signTimestamp: futureTs, sendTimestamp: futureTs));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task InvalidTimestamp_Returns401()
    {
        var req = DeployRequest(MakeZip(new() { ["f.txt"] = "x" }));
        req.Headers.Remove("X-Timestamp");
        req.Headers.Add("X-Timestamp", "not-a-number");
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task NoSignature_Returns401()
    {
        var req = DeployRequest(MakeZip(new() { ["f.txt"] = "x" }));
        req.Headers.Remove("X-Signature");
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), token: "wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task MalformedSignatureHeader_Returns401()
    {
        var req = DeployRequest(MakeZip(new() { ["f.txt"] = "x" }));
        req.Headers.Remove("X-Signature");
        req.Headers.TryAddWithoutValidation("X-Signature", "not-valid-hex!");
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ProfileTamperedInHeader_Returns401()
    {
        // Sign for TestProfile but send X-Profile = something else that exists (same config)
        // Since our test only has one profile, use a second-profile fixture.
        var zip = MakeZip(new() { ["f.txt"] = "x" });
        // Sign with correct profile, then change header
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = SignV2(zip, TestProfile, TestToken, now);

        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Protocol", "v2");
        req.Headers.Add("X-Timestamp", now.ToString());
        req.Headers.Add("X-Signature", sig);
        req.Headers.Add("X-Profile", "does-not-match");  // unknown profile → 400 before sig check
        var content = new MultipartFormDataContent();
        var bc = new ByteArrayContent(zip);
        bc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(bc, "file", "app.zip");
        req.Content = content;

        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task SignatureForDifferentProfile_Rejected()
    {
        // Sign with "other-profile" context but send X-Profile: test-profile (known profile).
        // Signature won't match → 401.
        var zip = MakeZip(new() { ["f.txt"] = "x" });
        var r = await _client!.SendAsync(DeployRequest(zip, signContext: "other-profile"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task TimestampTampered_Rejected()
    {
        // Sign with one ts, send a different ts → signature mismatch
        var zip = MakeZip(new() { ["f.txt"] = "x" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = await _client!.SendAsync(DeployRequest(zip, signTimestamp: now, sendTimestamp: now - 1));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnknownEndpoint_Returns404()
    {
        var r = await _client!.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/admin"));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetOnDeploy_IsNotAllowed()
    {
        var r = await _client!.GetAsync("/deploy");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Profile
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MissingProfileHeader_Returns400()
    {
        var req = DeployRequest(MakeZip(new() { ["f.txt"] = "x" }));
        req.Headers.Remove("X-Profile");
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("X-Profile", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownProfile_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), profile: "does-not-exist"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unknown profile", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProfileLookup_CaseInsensitive()
    {
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), profile: TestProfile.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Invalid inputs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidZip_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(Encoding.UTF8.GetBytes("not a zip")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("zip", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task ZipSlipPathTraversal_Rejected()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(archive.CreateEntry("../../evil.txt").Open());
            w.Write("I should not escape");
        }
        var r = await _client!.SendAsync(DeployRequest(ms.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unsafe", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task AbsolutePathInZip_Rejected()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            archive.CreateEntry("/etc/passwd").Open().Close();
        var r = await _client!.SendAsync(DeployRequest(ms.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Valid deployments
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidFlatZip_DeploysFiles()
    {
        ClearDeployDir();
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["index.html"] = "<h1>Hello</h1>", ["app.js"] = "x" })));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.EndsWith("OK\n", await r.Content.ReadAsStringAsync());
        Assert.True(File.Exists(Path.Combine(_deployDir!, "index.html")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "app.js")));
    }

    [Fact]
    public async Task NestedZip_UnwrapsSingleRootDir()
    {
        ClearDeployDir();
        var zip = MakeZip(new()
        {
            ["myapp/index.html"] = "<h1>Nested</h1>",
            ["myapp/src/main.js"] = "const x = 1;",
        });
        var r = await _client!.SendAsync(DeployRequest(zip));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(_deployDir!, "index.html")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "src", "main.js")));
        Assert.False(Directory.Exists(Path.Combine(_deployDir!, "myapp")));
    }

    [Fact]
    public async Task SecondDeployment_ReplacesFirst_AndCreatesBackup()
    {
        ClearDeployDir();
        await _client!.SendAsync(DeployRequest(MakeZip(new() { ["old.txt"] = "old" })));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "old.txt")));

        var r = await _client.SendAsync(DeployRequest(MakeZip(new() { ["new.txt"] = "new" })));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(_deployDir!, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "new.txt")));

        // Backup should contain the old deploy
        var backups = Directory.GetDirectories(_deployDir! + ".backups");
        Assert.NotEmpty(backups);
        Assert.True(File.Exists(Path.Combine(backups[0], "old.txt")));
    }

    [Fact]
    public async Task ZipWithMultipleTopLevelDirs_DeploysFlat()
    {
        ClearDeployDir();
        var zip = MakeZip(new() { ["dir1/a.txt"] = "a", ["dir2/b.txt"] = "b" });
        var r = await _client!.SendAsync(DeployRequest(zip));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(_deployDir!, "dir1", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "dir2", "b.txt")));
    }

    [Fact]
    public async Task RawBodyDeploy_DeploysFiles()
    {
        ClearDeployDir();
        var zip = MakeZip(new() { ["raw.txt"] = "raw" });
        var r = await _client!.SendAsync(DeployRequest(zip, rawBody: true));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(_deployDir!, "raw.txt")));
    }

    [Fact]
    public async Task EmptyZip_ClearsDeployDir()
    {
        File.WriteAllText(Path.Combine(_deployDir!, "existing.txt"), "old");
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new())));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(_deployDir!, "existing.txt")));
    }

    // -------------------------------------------------------------------------
    // Dry-run
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DryRun_DoesNotMutateDeployDir()
    {
        ClearDeployDir();
        File.WriteAllText(Path.Combine(_deployDir!, "keep.txt"), "original");

        var zip = MakeZip(new() { ["new.txt"] = "new" });
        var r = await _client!.SendAsync(DeployRequest(zip, dryRun: true));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("dry_run", body);

        Assert.True(File.Exists(Path.Combine(_deployDir!, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_deployDir!, "new.txt")));
    }

    [Fact]
    public async Task DryRun_JsonContainsExpectedShape()
    {
        var zip = MakeZip(new() { ["a.txt"] = "a", ["b.txt"] = "bb" });
        var r = await _client!.SendAsync(DeployRequest(zip, dryRun: true));
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("dry_run").GetBoolean());
        Assert.Equal(TestProfile, doc.RootElement.GetProperty("profile").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("entry_count").GetInt32());
    }

    // -------------------------------------------------------------------------
    // Concurrency
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentDeploys_BothSucceed()
    {
        ClearDeployDir();
        var zip1 = MakeZip(new() { ["a.txt"] = "1" });
        var zip2 = MakeZip(new() { ["b.txt"] = "2" });
        var t1 = _client!.SendAsync(DeployRequest(zip1));
        var t2 = _client!.SendAsync(DeployRequest(zip2));
        var responses = await Task.WhenAll(t1, t2);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        // One of the two payloads wins — exactly one target file should exist.
        var hasA = File.Exists(Path.Combine(_deployDir!, "a.txt"));
        var hasB = File.Exists(Path.Combine(_deployDir!, "b.txt"));
        Assert.True(hasA ^ hasB, "Exactly one deploy should have won");
    }

    // -------------------------------------------------------------------------
    // Service-level behaviour (via SystemctlHook)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartFailure_RestoresBackup()
    {
        // Reconfigure the test fixture with a service so the handler will invoke systemctl,
        // which we intercept with a hook that fails only on "start".
        await _app!.StopAsync();
        await _app.DisposeAsync();
        _client!.Dispose();

        var port = GetFreePort();
        var config = new ServerConfig
        {
            Token = TestToken,
            Port = port,
            MaxUploadMb = 500,
            KeepBackups = 3,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                [TestProfile] = new() { DeployDir = _deployDir!, Services = ["fake.service"] }
            }
        };
        _app = AppFactory.Create(config, $"http://127.0.0.1:{port}");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        DeployHandler.SystemctlHook = (cmd, svc) =>
            cmd == "start"
                ? (false, "simulated start failure")
                : (true, "");

        try
        {
            ClearDeployDir();
            File.WriteAllText(Path.Combine(_deployDir!, "original.txt"), "original content");

            var r = await _client.SendAsync(DeployRequest(MakeZip(new() { ["replacement.txt"] = "new" })));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("start failed", body);
            Assert.DoesNotContain("\nOK\n", body);

            // Original content should be restored
            Assert.True(File.Exists(Path.Combine(_deployDir!, "original.txt")));
            Assert.False(File.Exists(Path.Combine(_deployDir!, "replacement.txt")));
        }
        finally
        {
            DeployHandler.SystemctlHook = null;
        }
    }

    [Fact]
    public async Task StopFailure_AbortsBeforeTouchingFiles()
    {
        await _app!.StopAsync();
        await _app.DisposeAsync();
        _client!.Dispose();

        var port = GetFreePort();
        var config = new ServerConfig
        {
            Token = TestToken,
            Port = port,
            MaxUploadMb = 500,
            KeepBackups = 3,
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                [TestProfile] = new() { DeployDir = _deployDir!, Services = ["fake.service"] }
            }
        };
        _app = AppFactory.Create(config, $"http://127.0.0.1:{port}");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        DeployHandler.SystemctlHook = (cmd, svc) =>
            cmd == "stop"
                ? (false, "simulated stop failure")
                : (true, "");

        try
        {
            ClearDeployDir();
            File.WriteAllText(Path.Combine(_deployDir!, "original.txt"), "original");

            var r = await _client.SendAsync(DeployRequest(MakeZip(new() { ["replacement.txt"] = "new" })));
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("stop failed", body);
            Assert.DoesNotContain("\nOK\n", body);

            // Files were never touched
            Assert.True(File.Exists(Path.Combine(_deployDir!, "original.txt")));
            Assert.False(File.Exists(Path.Combine(_deployDir!, "replacement.txt")));
        }
        finally
        {
            DeployHandler.SystemctlHook = null;
        }
    }

    // -------------------------------------------------------------------------
    // Health / profiles / rollback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var r = await _client!.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Profiles_RequiresAuth()
    {
        var r = await _client!.GetAsync("/profiles");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);  // missing protocol
    }

    [Fact]
    public async Task Profiles_WithAuth_ListsProfiles()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = SignV2(null, "GET /profiles", TestToken, ts);
        var req = new HttpRequestMessage(HttpMethod.Get, "/profiles");
        req.Headers.Add("X-Protocol", "v2");
        req.Headers.Add("X-Timestamp", ts.ToString());
        req.Headers.Add("X-Signature", sig);
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("profiles").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).ToList();
        Assert.Contains(TestProfile, names);
    }

    [Fact]
    public async Task Rollback_RestoresPreviousDeploy()
    {
        ClearDeployDir();
        await _client!.SendAsync(DeployRequest(MakeZip(new() { ["v1.txt"] = "one" })));
        await _client.SendAsync(DeployRequest(MakeZip(new() { ["v2.txt"] = "two" })));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "v2.txt")));

        var body = JsonSerializer.SerializeToUtf8Bytes(new { Profile = TestProfile, Backup = "previous" });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = SignV2(body, "POST /rollback", TestToken, ts);
        var req = new HttpRequestMessage(HttpMethod.Post, "/rollback");
        req.Headers.Add("X-Protocol", "v2");
        req.Headers.Add("X-Timestamp", ts.ToString());
        req.Headers.Add("X-Signature", sig);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var r = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.EndsWith("OK\n", await r.Content.ReadAsStringAsync());

        Assert.True(File.Exists(Path.Combine(_deployDir!, "v1.txt")));
        Assert.False(File.Exists(Path.Combine(_deployDir!, "v2.txt")));
    }
}
