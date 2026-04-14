using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CitadelServer;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CitadelServer.Tests;

/// <summary>
/// Integration tests for the deploy endpoint.
/// Starts a real WebApplication on a free port for each test instance.
/// </summary>
public class DeployHandlerTests : IAsyncLifetime
{
    const string TestToken = "integ-test-token-citadel-xyz";

    WebApplication? _app;
    HttpClient? _client;
    string? _deployDir;

    public async Task InitializeAsync()
    {
        _deployDir = Directory.CreateTempSubdirectory("citadel_test_").FullName;

        var port = GetFreePort();
        var config = new ServerConfig { Token = TestToken, Port = port };
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

    HttpRequestMessage DeployRequest(byte[] zipBytes, string? token = null, string? deployDir = null, string? service = "test-svc")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestToken);
        req.Headers.Add("X-Deploy-Dir", deployDir ?? _deployDir!);
        if (service != null)
            req.Headers.Add("X-Service", service);

        var content = new MultipartFormDataContent();
        var zipContent = new ByteArrayContent(zipBytes);
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(zipContent, "file", "app.zip");
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

    static byte[] MakeZipWithEntry(string entryName, string content)
        => MakeZip(new Dictionary<string, string> { [entryName] = content });

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
    public async Task NoToken_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
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
    public async Task MalformedAuthHeader_Returns401()
    {
        // Missing "Bearer " prefix
        var req = new HttpRequestMessage(HttpMethod.Post, "/deploy");
        req.Headers.TryAddWithoutValidation("Authorization", TestToken);
        req.Headers.Add("X-Deploy-Dir", _deployDir!);
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
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
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
    // Invalid inputs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidZip_Returns400()
    {
        var r = await _client!.SendAsync(DeployRequest(System.Text.Encoding.UTF8.GetBytes("not a zip")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("zip", (await r.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Zip slip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ZipSlipPathTraversal_Rejected()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../../evil.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("I should not escape");
        }

        var r = await _client!.SendAsync(DeployRequest(ms.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unsafe", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsolutePathInZip_Rejected()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("/etc/passwd").Open().Close();
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
        Assert.Equal("deployed", await r.Content.ReadAsStringAsync());

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

        // Files should land at deployDir/index.html, NOT deployDir/myapp/index.html
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

    [Fact]
    public async Task NoServiceHeader_DeploysWithoutRestart()
    {
        ClearDeployDir();
        var zip = MakeZip(new() { ["index.html"] = "<h1>No service</h1>" });

        // Pass service: null so no X-Service header is sent
        var r = await _client!.SendAsync(DeployRequest(zip, service: null));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(File.Exists(Path.Combine(_deployDir!, "index.html")));
    }
}
