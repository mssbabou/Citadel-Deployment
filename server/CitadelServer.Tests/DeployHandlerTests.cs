using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
            Profiles = new Dictionary<string, ServerConfig.Profile>(StringComparer.OrdinalIgnoreCase)
            {
                [TestProfile] = new ServerConfig.Profile
                {
                    DeployDir = _deployDir,
                    Services = [],
                }
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
        if (_deployDir != null && Directory.Exists(_deployDir))
            Directory.Delete(_deployDir, recursive: true);
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

    static string ComputeSignature(byte[] body, string token)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    HttpRequestMessage DeployRequest(byte[] zipBytes, string? token = null, string? profile = null)
    {
        var content = new MultipartFormDataContent();
        var zipContent = new ByteArrayContent(zipBytes);
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(zipContent, "file", "app.zip");

        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Signature", ComputeSignature(zipBytes, token ?? TestToken));
        req.Headers.Add("X-Profile", profile ?? TestProfile);
        req.Content = content;
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
    }

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoSignature_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Profile", TestProfile);
        req.Content = new ByteArrayContent(MakeZip(new() { ["f.txt"] = "x" }));
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
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.TryAddWithoutValidation("X-Signature", "not-valid-hex!");
        req.Headers.Add("X-Profile", TestProfile);
        req.Content = new ByteArrayContent(MakeZip(new() { ["f.txt"] = "x" }));
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnknownEndpoint_Returns404()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin");
        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetMethod_IsNotAllowed()
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
        var zipBytes = MakeZip(new() { ["f.txt"] = "x" });
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Signature", ComputeSignature(zipBytes, TestToken));
        req.Content = new ByteArrayContent(zipBytes);
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

    // -------------------------------------------------------------------------
    // Invalid inputs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidZip_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(System.Text.Encoding.UTF8.GetBytes("not a zip")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("zip", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Zip slip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ZipSlipPathTraversal_Rejected()
    {
        using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../../evil.txt");
            await using var w = new StreamWriter(await entry.OpenAsync());
            await w.WriteAsync("I should not escape");
        }

        var r = await _client!.SendAsync(DeployRequest(ms.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unsafe", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsolutePathInZip_Rejected()
    {
        using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            (await archive.CreateEntry("/etc/passwd").OpenAsync()).Close();
        }

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
        var zip = MakeZip(new() { ["index.html"] = "<h1>Hello</h1>", ["app.js"] = "console.log('hi');" });

        var r = await _client!.SendAsync(DeployRequest(zip));
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
    public async Task SecondDeployment_ReplacesFirstDeployment()
    {
        ClearDeployDir();

        await _client!.SendAsync(DeployRequest(MakeZip(new() { ["old.txt"] = "old content" })));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "old.txt")));

        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["new.txt"] = "new content" })));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.False(File.Exists(Path.Combine(_deployDir!, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "new.txt")));
    }

    // -------------------------------------------------------------------------
    // Additional coverage
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProfileLookup_CaseInsensitive()
    {
        // ServerConfig.Load uses OrdinalIgnoreCase; verify the deploy endpoint honours it.
        var r = await _client!.SendAsync(DeployRequest(MakeZip(new() { ["f.txt"] = "x" }), profile: TestProfile.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task ZipWithMultipleTopLevelDirs_DeploysFlat()
    {
        // Two top-level dirs → no single-root unwrap; both dirs land directly in deploy_dir.
        ClearDeployDir();
        var zip = MakeZip(new()
        {
            ["dir1/a.txt"] = "a",
            ["dir2/b.txt"] = "b",
        });

        var r = await _client!.SendAsync(DeployRequest(zip));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.True(File.Exists(Path.Combine(_deployDir!, "dir1", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_deployDir!, "dir2", "b.txt")));
    }

    [Fact]
    public async Task RawBodyDeploy_DeploysFiles()
    {
        // POST raw zip bytes without multipart wrapper — exercises the non-multipart code path.
        ClearDeployDir();
        var zip = MakeZip(new() { ["raw.txt"] = "raw deploy" });

        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Signature", ComputeSignature(zip, TestToken));
        req.Headers.Add("X-Profile", TestProfile);
        req.Content = new ByteArrayContent(zip);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(_deployDir!, "raw.txt")));
    }

    [Fact]
    public async Task EmptyZip_ClearsDeployDir()
    {
        // An empty zip is valid; deploying it wipes the existing deploy dir.
        File.WriteAllText(Path.Combine(_deployDir!, "existing.txt"), "old");

        var zip = MakeZip(new());

        var r = await _client!.SendAsync(DeployRequest(zip));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(_deployDir!, "existing.txt")));
    }

    [Fact]
    public async Task MultipartWithNoFilename_Returns400()
    {
        // Multipart part missing filename= → ExtractFromMultipart returns null →
        // original multipart bytes written to tmpZip → not a valid zip → 400.
        var content = new MultipartFormDataContent();
        var zipContent = new ByteArrayContent(MakeZip(new() { ["f.txt"] = "x" }));
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(zipContent, "file"); // 2-arg overload: no filename= in Content-Disposition

        var bodyBytes = await content.ReadAsByteArrayAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Add("X-Signature", ComputeSignature(bodyBytes, TestToken));
        req.Headers.Add("X-Profile", TestProfile);
        req.Content = content;

        var r = await _client!.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
