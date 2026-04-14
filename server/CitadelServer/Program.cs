using CitadelServer;

// Handle --install before building the web app
if (args.Contains("--install"))
{
    InstallService();
    return;
}

var configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");

// First run: create default config.txt and exit
if (!File.Exists(configPath))
{
    File.WriteAllText(configPath, "# Configuration file for deploy-server\ntoken=your-secret-token-here\nport=9090\n");
    Console.WriteLine($"Created default config.txt at {configPath}");
    Console.WriteLine("Please edit it with your token and run again.");
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

// -------------------------------------------------------------------------
// --install: write and enable a systemd service for this binary
// -------------------------------------------------------------------------

static void InstallService()
{
    var binaryPath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
    var workingDir = Path.GetDirectoryName(binaryPath)!;
    const string serviceName = "deploy-server.service";
    var servicePath = $"/etc/systemd/system/{serviceName}";

    var unit = $"[Unit]\nDescription=Deploy Server\nAfter=network.target\n\n[Service]\nType=simple\nUser=root\nWorkingDirectory={workingDir}\nExecStart={binaryPath}\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=multi-user.target\n";

    try
    {
        File.WriteAllText(servicePath, unit);
        Run("systemctl", "daemon-reload");
        Run("systemctl", $"enable {serviceName}");
        Run("systemctl", $"start {serviceName}");
        Console.WriteLine($"✓ Service installed and started: {serviceName}");
        Console.WriteLine($"  View logs: journalctl -u {serviceName} -f");
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("✗ Permission denied. Run with sudo: sudo deploy-server --install");
        Environment.Exit(1);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"✗ Failed to install service: {ex.Message}");
        Environment.Exit(1);
    }

    static void Run(string file, string arguments)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = false,
        });
        p?.WaitForExit();
    }
}

// Required so WebApplicationFactory<Program> can reference this assembly entry point
public partial class Program { }
