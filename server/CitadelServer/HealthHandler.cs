using System.Reflection;
using System.Text.Json;

namespace CitadelServer;

/// <summary>
/// Unauthenticated health endpoint for load balancers / monitoring.
/// </summary>
public static class HealthHandler
{
    static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public static async Task HandleAsync(HttpContext ctx, DateTimeOffset startedAt)
    {
        var payload = new
        {
            status = "ok",
            version = Version,
            uptime_s = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        };
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
