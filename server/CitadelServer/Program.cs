using CitadelServer;

var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Error: config.txt not found at {configPath}");
    Console.Error.WriteLine("Run the installer: sudo bash install-server.sh");
    return;
}

var config = ServerConfig.Load(configPath);

if (string.IsNullOrEmpty(config.Token))
{
    Console.Error.WriteLine("Error: token not set in config.txt");
    return;
}

Console.WriteLine($"deploy server listening on :{config.Port}");
AppFactory.Create(config, $"http://0.0.0.0:{config.Port}").Run();

// Required so WebApplicationFactory<Program> can reference this assembly entry point
public partial class Program { }
