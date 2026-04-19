namespace CitadelServer;

/// <summary>
/// Builds the WebApplication with all routes registered.
/// Exposed as a public factory so tests can start the server directly.
/// </summary>
public static class AppFactory
{
    public static WebApplication Create(ServerConfig config, string listenUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(config);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.WebHost.UseUrls(listenUrl);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var audit = new AuditLog(config.AuditLogPath, logger);
        var startedAt = DateTimeOffset.UtcNow;

        app.MapGet("/health", ctx => HealthHandler.HandleAsync(ctx, startedAt));
        app.MapGet("/profiles", ctx => ProfilesHandler.HandleListAsync(ctx, config, logger));
        app.MapGet("/deploys", ctx => ProfilesHandler.HandleHistoryAsync(ctx, config, audit, logger));
        app.MapPost("/deploy", ctx => DeployHandler.HandleAsync(ctx, config, audit, logger));
        app.MapPost("/rollback", ctx => RollbackHandler.HandleAsync(ctx, config, audit, logger));

        return app;
    }
}
