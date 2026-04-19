using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CitadelServer;

/// <summary>
/// Append-only JSON-lines audit log with coarse size-based rotation.
/// Rotation: when the file exceeds 10 MiB, it's renamed to "audit.jsonl.1"
/// (overwriting any existing .1 file). One old file is kept.
/// </summary>
public sealed class AuditLog
{
    const long RotateBytes = 10L * 1024 * 1024;

    readonly string? _path;
    readonly object _lock = new();
    readonly ILogger _logger;

    public AuditLog(string? path, ILogger logger)
    {
        _path = string.IsNullOrEmpty(path) ? null : path;
        _logger = logger;
    }

    public bool Enabled => _path != null;

    public record Entry(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("ip")] string Ip,
        [property: JsonPropertyName("profile")] string Profile,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("message")] string? Message = null,
        [property: JsonPropertyName("bytes")] long? Bytes = null,
        [property: JsonPropertyName("duration_ms")] long? DurationMs = null);

    public void Append(Entry entry)
    {
        if (_path == null) return;

        var json = JsonSerializer.Serialize(entry);
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, json + "\n");

                if (new FileInfo(_path).Length > RotateBytes)
                    Rotate();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append audit entry");
        }
    }

    void Rotate()
    {
        if (_path == null) return;
        var rotated = _path + ".1";
        try
        {
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(_path, rotated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rotate audit log");
        }
    }

    /// <summary>Reads the last N entries for a given profile, newest first.</summary>
    public List<JsonElement> Tail(string profile, int max = 50)
    {
        if (_path == null || !File.Exists(_path))
            return [];

        var results = new List<JsonElement>();
        try
        {
            // File is small enough in practice (<=10 MiB) to read fully; reverse-scan in memory.
            string[] lines;
            lock (_lock) lines = File.ReadAllLines(_path);
            for (int i = lines.Length - 1; i >= 0 && results.Count < max; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement doc;
                try { doc = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (doc.TryGetProperty("profile", out var p) && p.GetString() == profile)
                    results.Add(doc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read audit log");
        }
        return results;
    }

    public static string NowTimestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
