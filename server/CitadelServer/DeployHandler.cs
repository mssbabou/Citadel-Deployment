using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CitadelServer;

public static class DeployHandler
{
    public static async Task HandleAsync(HttpContext ctx, ServerConfig config, ILogger logger)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // --- Profile lookup ---
        var profileName = ctx.Request.Headers["X-Profile"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(profileName))
        {
            logger.LogWarning("Missing X-Profile header from {Ip}", ip);
            await Respond(ctx, 400, "missing X-Profile header");
            return;
        }
        if (!config.Profiles.TryGetValue(profileName, out var profile))
        {
            logger.LogWarning("Unknown profile '{Profile}' from {Ip}", profileName, ip);
            await Respond(ctx, 400, $"unknown profile: {profileName}");
            return;
        }

        logger.LogInformation("Deploy request: profile={Profile} ip={Ip}", profileName, ip);

        var deployDir = profile.DeployDir;
        var services = profile.Services;

        // --- Read body ---
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        // --- Extract from multipart/form-data ---
        var contentType = ctx.Request.ContentType ?? "";
        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = ExtractFromMultipart(body, contentType);
            if (extracted == null)
            {
                logger.LogWarning("No file in multipart body from {Ip}", ip);
                await Respond(ctx, 400, "no file found in multipart body");
                return;
            }
            body = extracted;
        }

        // --- Auth: HMAC-SHA256 signature over zip bytes ---
        var sigHeader = ctx.Request.Headers["X-Signature"].ToString();
        if (string.IsNullOrEmpty(sigHeader))
        {
            logger.LogWarning("Unauthorized deploy attempt for profile '{Profile}' from {Ip}: no signature", profileName, ip);
            await Respond(ctx, 401, "unauthorized");
            return;
        }
        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.Token));
            var expectedSig = hmac.ComputeHash(body);
            var actualSig = Convert.FromHexString(sigHeader);
            if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            {
                logger.LogWarning("Unauthorized deploy attempt for profile '{Profile}' from {Ip}: signature mismatch", profileName, ip);
                await Respond(ctx, 401, "unauthorized");
                return;
            }
        }
        catch (FormatException)
        {
            logger.LogWarning("Unauthorized deploy attempt for profile '{Profile}' from {Ip}: invalid signature format", profileName, ip);
            await Respond(ctx, 401, "unauthorized");
            return;
        }

        // --- Write and validate temp zip ---
        var tmpZip = Path.Combine(Path.GetTempPath(), $"citadel_{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(tmpZip, body);

        if (!IsValidZip(tmpZip))
        {
            File.Delete(tmpZip);
            logger.LogWarning("Invalid zip from {Ip} for profile '{Profile}'", ip, profileName);
            await Respond(ctx, 400, "not a valid zip");
            return;
        }

        // --- Zip slip validation + extraction ---
        var tmpDir = Directory.CreateTempSubdirectory("citadel_").FullName;
        var realTmpDir = Path.GetFullPath(tmpDir);
        string? unsafeEntry = null;

        await using (var archive = await ZipFile.OpenReadAsync(tmpZip))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue;

                var entryPath = Path.GetFullPath(Path.Combine(tmpDir, entry.FullName));
                if (!entryPath.StartsWith(realTmpDir + Path.DirectorySeparatorChar))
                {
                    unsafeEntry = entry.FullName;
                    break;
                }
            }

            if (unsafeEntry == null)
                await archive.ExtractToDirectoryAsync(tmpDir, overwriteFiles: true);
        }

        File.Delete(tmpZip);

        if (unsafeEntry != null)
        {
            Directory.Delete(tmpDir, recursive: true);
            logger.LogWarning("Unsafe zip entry '{Entry}' from {Ip} for profile '{Profile}' — rejected", unsafeEntry, ip, profileName);
            await Respond(ctx, 400, "unsafe path in zip");
            return;
        }

        // --- Single root dir unwrapping ---
        var entries = Directory.GetFileSystemEntries(tmpDir);
        var source = tmpDir;
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            source = entries[0];

        // -----------------------------------------------------------------------
        // All validation done. Stream deploy progress from here on.
        // HTTP status is 200; errors are reported as "ERROR: ..." lines.
        // -----------------------------------------------------------------------
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";

        async Task Log(string line)
        {
            await ctx.Response.WriteAsync(line + "\n");
            await ctx.Response.Body.FlushAsync();
        }

        try
        {
            // --- Stop services ---
            foreach (var svc in services)
            {
                var ok = RunSystemctl("stop", svc);
                logger.LogInformation("Stopping {Service}: {Status}", svc, ok ? "ok" : "failed");
                await Log($"Stopping {svc}: {(ok ? "ok" : "failed")}");
            }

            // --- Replace files ---
            logger.LogInformation("Replacing files in {DeployDir}", deployDir);
            if (Directory.Exists(deployDir))
                Directory.Delete(deployDir, recursive: true);
            CopyDirectory(source, deployDir);
            await Log("Replacing files: ok");

            // --- Post-update command ---
            if (!string.IsNullOrEmpty(profile.PostUpdateCommand))
            {
                logger.LogInformation("Running post-update: {Command}", profile.PostUpdateCommand);
                await Log($"Running post-update: {profile.PostUpdateCommand}");
                var (cmdOk, cmdOutput) = await RunCommand(profile.PostUpdateCommand, deployDir);
                if (!string.IsNullOrEmpty(cmdOutput))
                    foreach (var line in cmdOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        await Log($"  {line.TrimEnd()}");
                if (!cmdOk)
                {
                    logger.LogError("Post-update command failed for profile '{Profile}'", profileName);
                    await Log("ERROR: post-update command failed");
                    foreach (var svc in services)
                        RunSystemctl("start", svc);
                    return;
                }
            }

            // --- Start services ---
            foreach (var svc in services)
            {
                var ok = RunSystemctl("start", svc);
                logger.LogInformation("Starting {Service}: {Status}", svc, ok ? "ok" : "failed");
                await Log($"Starting {svc}: {(ok ? "ok" : "failed")}");
            }

            logger.LogInformation("Deploy complete for profile '{Profile}'", profileName);
            await Log("OK");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception during deploy for profile '{Profile}'", profileName);
            try { await Log($"ERROR: {ex.Message}"); } catch { }
            foreach (var svc in services)
                RunSystemctl("start", svc);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static Task Respond(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        return ctx.Response.WriteAsync(message);
    }

    static bool IsValidZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static byte[]? ExtractFromMultipart(byte[] body, string contentType)
    {
        const string boundaryPrefix = "boundary=";
        var idx = contentType.IndexOf(boundaryPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var boundary = contentType[(idx + boundaryPrefix.Length)..].Trim().Trim('"');

        var separator = Encoding.ASCII.GetBytes($"--{boundary}");
        var parts = SplitBytes(body, separator);

        foreach (var part in parts)
        {
            if (part.AsSpan().IndexOf("filename="u8) < 0)
                continue;

            var headerEnd = IndexOf(part, "\r\n\r\n"u8);
            if (headerEnd < 0) continue;

            var payload = part[(headerEnd + 4)..];
            if (payload.Length >= 2 && payload[^2] == '\r' && payload[^1] == '\n')
                payload = payload[..^2];

            return payload;
        }

        return null;
    }

    static List<byte[]> SplitBytes(byte[] source, byte[] separator)
    {
        var result = new List<byte[]>();
        int start = 0;
        while (true)
        {
            int pos = IndexOf(source.AsSpan()[start..], separator);
            if (pos < 0)
            {
                result.Add(source[start..]);
                break;
            }
            result.Add(source[start..(start + pos)]);
            start += pos + separator.Length;
        }
        return result;
    }

    static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (int i = 0; i <= source.Length - value.Length; i++)
        {
            if (source[i..].StartsWith(value))
                return i;
        }
        return -1;
    }

    static bool RunSystemctl(string command, string service)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"{command} {service}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(TimeSpan.FromSeconds(15));
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    static async Task<(bool Success, string Output)> RunCommand(string command, string workingDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", command },
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var exited = await Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync()),
                Task.Delay(TimeSpan.FromSeconds(60)));

            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                return (false, "timed out after 60s");
            }

            var output = ((await stdoutTask) + (await stderrTask)).Trim();
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
