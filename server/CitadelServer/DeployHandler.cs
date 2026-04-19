using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CitadelServer;

public static class DeployHandler
{
    public static async Task HandleAsync(HttpContext ctx, ServerConfig config, AuditLog audit, ILogger logger)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // --- Profile lookup ---
        var profileName = ctx.Request.Headers["X-Profile"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(profileName))
        {
            logger.LogWarning("Missing X-Profile header from {Ip}", ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, "", "deploy", "missing_profile"));
            await Respond(ctx, 400, "missing X-Profile header");
            return;
        }
        if (!config.Profiles.TryGetValue(profileName, out var profile))
        {
            logger.LogWarning("Unknown profile '{Profile}' from {Ip}", profileName, ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "unknown_profile"));
            await Respond(ctx, 400, $"unknown profile: {profileName}");
            return;
        }

        logger.LogInformation("Deploy request: profile={Profile} ip={Ip}", profileName, ip);

        // --- Parse auth headers ---
        var (authResult, authHeaders) = Auth.ParseHeaders(ctx);
        if (authResult != Auth.Result.Ok)
        {
            logger.LogWarning("Auth failed ({Reason}) for profile '{Profile}' from {Ip}", authResult, profileName, ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "auth_fail", Auth.Describe(authResult)));
            await Respond(ctx, Auth.StatusCode(authResult), Auth.Describe(authResult));
            return;
        }

        // --- Upload size guard (early) ---
        long maxBytes = (long)config.MaxUploadMb * 1024 * 1024;
        if (ctx.Request.ContentLength is long advertised && advertised > maxBytes)
        {
            logger.LogWarning("Upload advertised {Bytes} > {Max} from {Ip}", advertised, maxBytes, ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "too_large", Bytes: advertised));
            await Respond(ctx, 413, $"payload too large (max {config.MaxUploadMb} MB)");
            return;
        }

        // --- Stream body to a temp file, enforcing the size limit ---
        var bodyPath = Path.Combine(Path.GetTempPath(), $"citadel_body_{Guid.NewGuid():N}.bin");
        long bodyBytes;
        try
        {
            bodyBytes = await StreamBodyToFileAsync(ctx.Request.Body, bodyPath, maxBytes, ctx.RequestAborted);
        }
        catch (UploadTooLargeException)
        {
            TryDelete(bodyPath);
            logger.LogWarning("Upload exceeded limit while streaming from {Ip}", ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "too_large"));
            await Respond(ctx, 413, $"payload too large (max {config.MaxUploadMb} MB)");
            return;
        }

        // --- Multipart unwrap: isolate the inner zip bytes ---
        string zipPath;
        try
        {
            zipPath = await UnwrapMultipartIfNeededAsync(bodyPath, ctx.Request.ContentType ?? "");
        }
        catch (InvalidMultipartException ex)
        {
            TryDelete(bodyPath);
            logger.LogWarning("Malformed multipart from {Ip}: {Msg}", ip, ex.Message);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "malformed_multipart", ex.Message));
            await Respond(ctx, 400, "malformed multipart");
            return;
        }

        // --- Signature check (HMAC is over the inner zip bytes) ---
        var sigResult = await VerifySignatureAsync(zipPath, config.Token, authHeaders.Timestamp, profileName, authHeaders.Signature);
        if (sigResult != Auth.Result.Ok)
        {
            TryDelete(bodyPath);
            if (zipPath != bodyPath) TryDelete(zipPath);
            logger.LogWarning("Unauthorized deploy for profile '{Profile}' from {Ip}: {Reason}", profileName, ip, Auth.Describe(sigResult));
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "auth_fail", Auth.Describe(sigResult)));
            await Respond(ctx, Auth.StatusCode(sigResult), Auth.Describe(sigResult));
            return;
        }

        // --- Validate zip + extract with zip-slip protection ---
        if (!IsValidZip(zipPath))
        {
            TryDelete(bodyPath);
            if (zipPath != bodyPath) TryDelete(zipPath);
            logger.LogWarning("Invalid zip from {Ip} for profile '{Profile}'", ip, profileName);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "invalid_zip"));
            await Respond(ctx, 400, "not a valid zip");
            return;
        }

        string tmpDir;
        try
        {
            tmpDir = await ExtractZipSafelyAsync(zipPath);
        }
        catch (UnsafeZipEntryException ex)
        {
            TryDelete(bodyPath);
            if (zipPath != bodyPath) TryDelete(zipPath);
            logger.LogWarning("Unsafe zip entry '{Entry}' from {Ip} for profile '{Profile}' — rejected", ex.Entry, ip, profileName);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "unsafe_path", ex.Entry));
            await Respond(ctx, 400, "unsafe path in zip");
            return;
        }

        // Body + zip files no longer needed
        TryDelete(bodyPath);
        if (zipPath != bodyPath) TryDelete(zipPath);

        var source = UnwrapSingleRootDir(tmpDir);

        // --- Dry-run: respond with JSON summary, skip all side effects ---
        if (IsDryRun(ctx))
        {
            var summary = BuildDryRunSummary(source, profile, profileName, bodyBytes);
            TryDeleteDir(tmpDir);
            logger.LogInformation("Dry-run OK for profile '{Profile}' from {Ip}", profileName, ip);
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", "dry_run_ok", Bytes: bodyBytes));
            await RespondJson(ctx, 200, summary);
            return;
        }

        // ------------------------------------------------------------------
        // Side-effect phase: serialize per-profile.
        // ------------------------------------------------------------------
        var sem = DeployState.Lock(profileName);
        await sem.WaitAsync(ctx.RequestAborted);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";

        async Task Log(string line)
        {
            await ctx.Response.WriteAsync(line + "\n");
            await ctx.Response.Body.FlushAsync();
        }

        string result = "deploy_error";
        string? resultMessage = null;
        string? backupDir = null;
        var deployDir = profile.DeployDir;

        try
        {
            // Stop services (abort before touching files if any fail)
            var stopFailures = new List<string>();
            foreach (var svc in profile.Services)
            {
                var (ok, err) = RunSystemctl("stop", svc);
                logger.LogInformation("Stopping {Service}: {Status}", svc, ok ? "ok" : $"failed: {err}");
                await Log(ok ? $"Stopping {svc}: ok" : $"Stopping {svc}: failed: {err}");
                if (!ok) stopFailures.Add(svc);
            }
            if (stopFailures.Count > 0)
            {
                await Log($"ERROR: stop failed for {string.Join(", ", stopFailures)}; aborting before file changes");
                foreach (var svc in profile.Services) RunSystemctl("start", svc);
                result = "stop_failed";
                resultMessage = string.Join(",", stopFailures);
                return;
            }

            // Atomic swap: existing deploy_dir → backups/backup-<ts>
            if (Directory.Exists(deployDir))
            {
                Directory.CreateDirectory(DeployState.BackupRoot(deployDir));
                backupDir = Path.Combine(DeployState.BackupRoot(deployDir), DeployState.NewBackupName());
                Directory.Move(deployDir, backupDir);
            }

            try
            {
                Directory.CreateDirectory(deployDir);
                CopyDirectory(source, deployDir);
                await Log("Replacing files: ok");
            }
            catch (Exception copyEx)
            {
                logger.LogError(copyEx, "Copy failed; restoring backup");
                TryDeleteDir(deployDir);
                if (backupDir != null && Directory.Exists(backupDir))
                {
                    Directory.Move(backupDir, deployDir);
                    backupDir = null;
                }
                throw;
            }

            // Post-update command
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
                    await Log("ERROR: post-update command failed; restoring previous deploy");
                    TryDeleteDir(deployDir);
                    if (backupDir != null && Directory.Exists(backupDir))
                    {
                        Directory.Move(backupDir, deployDir);
                        backupDir = null;
                    }
                    foreach (var svc in profile.Services) RunSystemctl("start", svc);
                    result = "post_update_failed";
                    return;
                }
            }

            // Start services
            var startFailures = new List<string>();
            foreach (var svc in profile.Services)
            {
                var (ok, err) = RunSystemctl("start", svc);
                logger.LogInformation("Starting {Service}: {Status}", svc, ok ? "ok" : $"failed: {err}");
                await Log(ok ? $"Starting {svc}: ok" : $"Starting {svc}: failed: {err}");
                if (!ok) startFailures.Add(svc);
            }

            if (startFailures.Count > 0)
            {
                await Log($"ERROR: start failed for {string.Join(", ", startFailures)}; restoring previous deploy");
                foreach (var svc in profile.Services) RunSystemctl("stop", svc);
                TryDeleteDir(deployDir);
                if (backupDir != null && Directory.Exists(backupDir))
                {
                    Directory.Move(backupDir, deployDir);
                    backupDir = null;
                }
                foreach (var svc in profile.Services) RunSystemctl("start", svc);
                result = "start_failed";
                resultMessage = string.Join(",", startFailures);
                return;
            }

            // Success — keep backup, prune oldest
            DeployState.PruneBackups(deployDir, config.KeepBackups);

            logger.LogInformation("Deploy complete for profile '{Profile}'", profileName);
            await Log("OK");
            result = "ok";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception during deploy for profile '{Profile}'", profileName);
            try { await Log($"ERROR: {ex.Message}"); } catch { }
            foreach (var svc in profile.Services) RunSystemctl("start", svc);
            resultMessage = ex.Message;
        }
        finally
        {
            TryDeleteDir(tmpDir);
            sem.Release();
            var durationMs = (long)(DateTimeOffset.UtcNow - startUtc).TotalMilliseconds;
            audit.Append(new(AuditLog.NowTimestamp(), ip, profileName, "deploy", result,
                Message: resultMessage, Bytes: bodyBytes, DurationMs: durationMs));
        }
    }

    // -------------------------------------------------------------------------
    // Dry-run
    // -------------------------------------------------------------------------

    static bool IsDryRun(HttpContext ctx)
    {
        var v = ctx.Request.Headers["X-DryRun"].ToString();
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(v, "1", StringComparison.Ordinal);
    }

    static object BuildDryRunSummary(string source, ServerConfig.Profile profile, string profileName, long bodyBytes)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        return new
        {
            dry_run = true,
            profile = profileName,
            deploy_dir = profile.DeployDir,
            services = profile.Services,
            post_update_command = string.IsNullOrEmpty(profile.PostUpdateCommand) ? null : profile.PostUpdateCommand,
            entry_count = files.Length,
            total_bytes = totalBytes,
            upload_bytes = bodyBytes,
        };
    }

    // -------------------------------------------------------------------------
    // Streaming helpers
    // -------------------------------------------------------------------------

    sealed class UploadTooLargeException : Exception { }
    sealed class InvalidMultipartException(string message) : Exception(message);
    sealed class UnsafeZipEntryException(string entry) : Exception { public string Entry { get; } = entry; }

    static async Task<long> StreamBodyToFileAsync(Stream body, string destPath, long maxBytes, CancellationToken ct)
    {
        await using var fs = File.Create(destPath);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new UploadTooLargeException();
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    static async Task<Auth.Result> VerifySignatureAsync(string zipPath, string token, long timestamp, string context, byte[] expected)
    {
        using var hmac = Auth.BeginHmac(token, timestamp, context);
        await using (var fs = File.OpenRead(zipPath))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
                hmac.AppendData(buffer.AsSpan(0, read));
        }
        return Auth.FinalizeAndCompare(hmac, expected);
    }

    // -------------------------------------------------------------------------
    // Multipart extraction (writes inner zip to a second temp file)
    // -------------------------------------------------------------------------

    static async Task<string> UnwrapMultipartIfNeededAsync(string bodyPath, string contentType)
    {
        if (!contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return bodyPath;

        const string boundaryPrefix = "boundary=";
        var idx = contentType.IndexOf(boundaryPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) throw new InvalidMultipartException("no boundary in Content-Type");
        var boundary = contentType[(idx + boundaryPrefix.Length)..].Trim().Trim('"');

        var bytes = await File.ReadAllBytesAsync(bodyPath);
        var inner = ExtractFromMultipart(bytes, boundary);
        if (inner == null) throw new InvalidMultipartException("no file part with filename=");

        var zipPath = bodyPath + ".zip";
        await File.WriteAllBytesAsync(zipPath, inner);
        return zipPath;
    }

    static byte[]? ExtractFromMultipart(byte[] body, string boundary)
    {
        var separator = Encoding.ASCII.GetBytes($"--{boundary}");
        int start = 0;
        while (true)
        {
            int idx = IndexOf(body.AsSpan(start), separator);
            if (idx < 0) return null;
            int partStart = start + idx + separator.Length;

            int nextIdx = IndexOf(body.AsSpan(partStart), separator);
            int partEnd = nextIdx < 0 ? body.Length : partStart + nextIdx;

            var partSpan = body.AsSpan(partStart, partEnd - partStart);
            if (partSpan.IndexOf("filename="u8) >= 0)
            {
                int headerEnd = IndexOf(partSpan, "\r\n\r\n"u8);
                if (headerEnd >= 0)
                {
                    int payloadStart = partStart + headerEnd + 4;
                    int payloadEnd = partEnd;
                    if (payloadEnd - payloadStart >= 2 && body[payloadEnd - 2] == '\r' && body[payloadEnd - 1] == '\n')
                        payloadEnd -= 2;
                    return body[payloadStart..payloadEnd];
                }
            }

            if (nextIdx < 0) return null;
            start = partEnd;
        }
    }

    static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (int i = 0; i <= source.Length - value.Length; i++)
            if (source[i..].StartsWith(value)) return i;
        return -1;
    }

    // -------------------------------------------------------------------------
    // Zip handling
    // -------------------------------------------------------------------------

    static bool IsValidZip(string path)
    {
        try { using var _ = ZipFile.OpenRead(path); return true; }
        catch { return false; }
    }

    static async Task<string> ExtractZipSafelyAsync(string zipPath)
    {
        var tmpDir = Directory.CreateTempSubdirectory("citadel_").FullName;
        var realTmpDir = Path.GetFullPath(tmpDir);

        await using var archive = await ZipFile.OpenReadAsync(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            var entryPath = Path.GetFullPath(Path.Combine(tmpDir, entry.FullName));
            if (!entryPath.StartsWith(realTmpDir + Path.DirectorySeparatorChar))
            {
                Directory.Delete(tmpDir, recursive: true);
                throw new UnsafeZipEntryException(entry.FullName);
            }
        }
        await archive.ExtractToDirectoryAsync(tmpDir, overwriteFiles: true);
        return tmpDir;
    }

    static string UnwrapSingleRootDir(string tmpDir)
    {
        var entries = Directory.GetFileSystemEntries(tmpDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];
        return tmpDir;
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

    // -------------------------------------------------------------------------
    // Process helpers
    // -------------------------------------------------------------------------

    // Test hook — lets tests inject systemctl behavior without needing systemd.
    // Returns (success, stderr). If null, the real systemctl binary is invoked.
    public static Func<string, string, (bool ok, string stderr)>? SystemctlHook { get; set; }

    internal static (bool ok, string stderr) RunSystemctl(string command, string service)
    {
        if (SystemctlHook != null)
        {
            try { return SystemctlHook(command, service); }
            catch (Exception ex) { return (false, ex.Message); }
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = $"{command} {service}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };
            proc.Start();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(15));
            return (proc.HasExited && proc.ExitCode == 0, stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static async Task<(bool Success, string Output)> RunCommand(string command, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", command },
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAny(
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

    // -------------------------------------------------------------------------
    // Small response / cleanup helpers
    // -------------------------------------------------------------------------

    static Task Respond(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        return ctx.Response.WriteAsync(message);
    }

    static Task RespondJson(HttpContext ctx, int status, object payload)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
