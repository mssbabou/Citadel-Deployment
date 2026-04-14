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
        builder.WebHost.UseUrls(listenUrl);

        var app = builder.Build();

        app.MapPost("/deploy", (HttpContext ctx) =>
            DeployHandler.HandleAsync(ctx, config.Token));

        return app;
    }
}
