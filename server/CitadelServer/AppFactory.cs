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

        app.MapPost("/deploy", ctx =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
            return DeployHandler.HandleAsync(ctx, config, logger);
        });

        return app;
    }
}
