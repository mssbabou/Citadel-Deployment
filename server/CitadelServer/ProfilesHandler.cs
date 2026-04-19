using System.Text.Json;

namespace CitadelServer;

/// <summary>
/// Authed read-only endpoints for listing profiles and inspecting deploy history.
/// </summary>
public static class ProfilesHandler
{
    public static async Task HandleListAsync(HttpContext ctx, ServerConfig config, ILogger logger)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var res = Auth.VerifyNoBody(ctx, config.Token, "GET /profiles");
        if (res != Auth.Result.Ok)
        {
            logger.LogWarning("Auth failed for GET /profiles from {Ip}: {Reason}", ip, res);
            ctx.Response.StatusCode = Auth.StatusCode(res);
            await ctx.Response.WriteAsync(Auth.Describe(res));
            return;
        }

        var payload = new
        {
            profiles = config.Profiles.Select(kv => new
            {
                name = kv.Key,
                deploy_dir = kv.Value.DeployDir,
                services = kv.Value.Services,
                has_post_update = !string.IsNullOrEmpty(kv.Value.PostUpdateCommand),
            }).ToArray(),
        };
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    public static async Task HandleHistoryAsync(HttpContext ctx, ServerConfig config, AuditLog audit, ILogger logger)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var profile = ctx.Request.Query["profile"].ToString();
        if (string.IsNullOrEmpty(profile))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("missing ?profile= query parameter");
            return;
        }

        var res = Auth.VerifyNoBody(ctx, config.Token, $"GET /deploys?profile={profile}");
        if (res != Auth.Result.Ok)
        {
            logger.LogWarning("Auth failed for GET /deploys from {Ip}: {Reason}", ip, res);
            ctx.Response.StatusCode = Auth.StatusCode(res);
            await ctx.Response.WriteAsync(Auth.Describe(res));
            return;
        }

        var entries = audit.Tail(profile, 50);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { profile, entries }));
    }
}
