using System.Text.Json;

namespace CitadelServer;

/// <summary>
/// POST /rollback — restore a previous deploy by renaming a backup directory
/// back into place. Body: {"profile":"name","backup":"backup-...|previous"}.
/// Serialized via the same per-profile lock as /deploy.
/// </summary>
public static class RollbackHandler
{
    public sealed record Request(string Profile, string Backup);

    public static async Task HandleAsync(HttpContext ctx, ServerConfig config, AuditLog audit, ILogger logger)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Read body
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        // Auth: signs (ts || "POST /rollback" || body)
        var authRes = Auth.VerifyWithBody(ctx, config.Token, "POST /rollback", body);
        if (authRes != Auth.Result.Ok)
        {
            logger.LogWarning("Auth failed for POST /rollback from {Ip}: {Reason}", ip, authRes);
            audit.Append(new(AuditLog.NowTimestamp(), ip, "", "rollback", "auth_fail", Auth.Describe(authRes)));
            ctx.Response.StatusCode = Auth.StatusCode(authRes);
            await ctx.Response.WriteAsync(Auth.Describe(authRes));
            return;
        }

        Request? req;
        try { req = JsonSerializer.Deserialize<Request>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { req = null; }
        if (req == null || string.IsNullOrEmpty(req.Profile) || string.IsNullOrEmpty(req.Backup))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("body must be JSON with profile and backup fields");
            return;
        }

        if (!config.Profiles.TryGetValue(req.Profile, out var profile))
        {
            audit.Append(new(AuditLog.NowTimestamp(), ip, req.Profile, "rollback", "unknown_profile"));
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"unknown profile: {req.Profile}");
            return;
        }

        // Resolve backup name
        var backups = DeployState.ListBackups(profile.DeployDir);
        string? target = req.Backup == "previous" ? backups.FirstOrDefault() : backups.FirstOrDefault(b => b == req.Backup);
        if (target == null)
        {
            audit.Append(new(AuditLog.NowTimestamp(), ip, req.Profile, "rollback", "backup_not_found", req.Backup));
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync($"backup not found: {req.Backup}");
            return;
        }

        var sem = DeployState.Lock(req.Profile);
        await sem.WaitAsync(ctx.RequestAborted);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        async Task Log(string line)
        {
            await ctx.Response.WriteAsync(line + "\n");
            await ctx.Response.Body.FlushAsync();
        }

        var result = "rollback_error";
        string? resultMessage = null;
        try
        {
            var deployDir = profile.DeployDir;
            var backupRoot = DeployState.BackupRoot(deployDir);
            var backupPath = Path.Combine(backupRoot, target);

            foreach (var svc in profile.Services)
            {
                var (ok, err) = DeployHandler.RunSystemctl("stop", svc);
                await Log(ok ? $"Stopping {svc}: ok" : $"Stopping {svc}: failed: {err}");
            }

            // Move current deploy aside as a new backup, then move the target backup into place.
            string? aside = null;
            if (Directory.Exists(deployDir))
            {
                aside = Path.Combine(backupRoot, DeployState.NewBackupName() + "-preroll");
                Directory.Move(deployDir, aside);
            }
            Directory.Move(backupPath, deployDir);
            await Log($"Restored backup: {target}");

            foreach (var svc in profile.Services)
            {
                var (ok, err) = DeployHandler.RunSystemctl("start", svc);
                await Log(ok ? $"Starting {svc}: ok" : $"Starting {svc}: failed: {err}");
            }

            DeployState.PruneBackups(deployDir, config.KeepBackups);
            await Log("OK");
            result = "ok";
            resultMessage = target;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rollback failed for profile '{Profile}'", req.Profile);
            try { await Log($"ERROR: {ex.Message}"); } catch { }
            foreach (var svc in profile.Services) DeployHandler.RunSystemctl("start", svc);
            resultMessage = ex.Message;
        }
        finally
        {
            sem.Release();
            audit.Append(new(AuditLog.NowTimestamp(), ip, req.Profile, "rollback", result, resultMessage));
        }
    }
}
