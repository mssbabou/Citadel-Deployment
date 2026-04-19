using System.Reflection;
using CitadelServer;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "--version":
        case "-v":
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version?.ToString() ?? "0.0.0";
            Console.WriteLine($"deploy-server {version}");
            return;
        case "--help":
        case "-h":
            Console.WriteLine("deploy-server — Citadel deployment server");
            Console.WriteLine();
            Console.WriteLine("Usage: deploy-server [--version|--help|--validate-config]");
            Console.WriteLine();
            Console.WriteLine("  (no args)          start the server; reads config.toml from the binary's directory");
            Console.WriteLine("  --version          print version and exit");
            Console.WriteLine("  --validate-config  parse config.toml, report errors, exit 0 or 1");
            return;
        case "--validate-config":
            var vpath = Path.Combine(AppContext.BaseDirectory, "config.toml");
            if (!File.Exists(vpath))
            {
                Console.Error.WriteLine($"Error: config.toml not found at {vpath}");
                Environment.Exit(1);
                return;
            }
            var cfg = ServerConfig.Load(vpath);
            var errs = cfg.Validate();
            if (errs.Count == 0)
            {
                Console.WriteLine("config.toml OK");
                return;
            }
            foreach (var e in errs) Console.Error.WriteLine($"  {e}");
            Environment.Exit(1);
            return;
    }
}

var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Error: config.toml not found at {configPath}");
    Console.Error.WriteLine("Run the installer: sudo bash install-server.sh");
    return;
}

var config = ServerConfig.Load(configPath);

var errors = config.Validate();
if (errors.Count > 0)
{
    Console.Error.WriteLine("Error: invalid config.toml:");
    foreach (var e in errors) Console.Error.WriteLine($"  {e}");
    return;
}

// Default the audit log path to a file next to config.toml, if not overridden.
if (string.IsNullOrEmpty(config.AuditLogPath))
{
    config = new ServerConfig
    {
        Token = config.Token,
        Port = config.Port,
        MaxUploadMb = config.MaxUploadMb,
        KeepBackups = config.KeepBackups,
        AuditLogPath = Path.Combine(AppContext.BaseDirectory, "audit.jsonl"),
        Profiles = config.Profiles,
    };
}

Console.WriteLine($"deploy server listening on :{config.Port}");
AppFactory.Create(config, $"http://0.0.0.0:{config.Port}").Run();
