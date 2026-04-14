using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CitadelServer;

public static class DeployHandler
{
    public static async Task HandleAsync(HttpContext ctx, string token)
    {
        // --- Auth ---
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        var expected = $"Bearer {token}";
        if (!ConstantTimeEquals(authHeader, expected))
        {
            await Respond(ctx, 401, "unauthorized");
            return;
        }

        var service = ctx.Request.Headers["X-Service"].FirstOrDefault() ?? "";
        var deployDir = ctx.Request.Headers["X-Deploy-Dir"].FirstOrDefault() ?? "/var/www";

        string? tmpZip = null;
        string? tmpDir = null;

        try
        {
            // --- Read body ---
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var body = ms.ToArray();

            // --- Extract from multipart/form-data ---
            var contentType = ctx.Request.ContentType ?? "";
            if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var extracted = ExtractFromMultipart(body, contentType);
                if (extracted != null)
                    body = extracted;
            }

            // --- Write temp zip ---
            tmpZip = Path.Combine(Path.GetTempPath(), $"citadel_{Guid.NewGuid():N}.zip");
            await File.WriteAllBytesAsync(tmpZip, body);

            if (!IsValidZip(tmpZip))
            {
                File.Delete(tmpZip);
                tmpZip = null;
                await Respond(ctx, 400, "not a valid zip");
                return;
            }

            // --- Zip slip validation (check all paths before extracting) ---
            tmpDir = Directory.CreateTempSubdirectory("citadel_").FullName;
            var realTmpDir = Path.GetFullPath(tmpDir);
            string? unsafeEntry = null;

            using (var archive = ZipFile.OpenRead(tmpZip))
            {
                foreach (var entry in archive.Entries)
                {
                    // Skip directory entries
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        continue;

                    var entryPath = Path.GetFullPath(Path.Combine(tmpDir, entry.FullName));
                    if (!entryPath.StartsWith(realTmpDir + Path.DirectorySeparatorChar))
                    {
                        unsafeEntry = entry.FullName;
                        break;
                    }
                }

                if (unsafeEntry == null)
                    archive.ExtractToDirectory(tmpDir, overwriteFiles: true);
            }

            File.Delete(tmpZip);
            tmpZip = null;

            if (unsafeEntry != null)
            {
                await Respond(ctx, 400, "unsafe path in zip");
                return;
            }

            // --- Single root dir unwrapping ---
            var entries = Directory.GetFileSystemEntries(tmpDir);
            var source = tmpDir;
            if (entries.Length == 1 && Directory.Exists(entries[0]))
                source = entries[0];

            // --- Stop service ---
            if (!string.IsNullOrEmpty(service))
                RunSystemctl("stop", service);

            // --- Replace files ---
            if (Directory.Exists(deployDir))
                Directory.Delete(deployDir, recursive: true);
            CopyDirectory(source, deployDir);

            // --- Start service ---
            if (!string.IsNullOrEmpty(service))
                RunSystemctl("start", service);

            await Respond(ctx, 200, "deployed");
        }
        catch (Exception ex)
        {
            // Recovery: attempt to restart service even on failure
            if (!string.IsNullOrEmpty(service))
                RunSystemctl("start", service);

            await Respond(ctx, 500, ex.Message);
        }
        finally
        {
            if (tmpZip != null && File.Exists(tmpZip))
                try { File.Delete(tmpZip); } catch { }

            if (tmpDir != null && Directory.Exists(tmpDir))
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static Task Respond(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        return ctx.Response.WriteAsync(message);
    }

    static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        // Pad the shorter one so FixedTimeEquals receives equal-length spans
        int len = Math.Max(aBytes.Length, bBytes.Length);
        var aPadded = new byte[len];
        var bPadded = new byte[len];
        aBytes.CopyTo(aPadded, 0);
        bBytes.CopyTo(bPadded, 0);
        return CryptographicOperations.FixedTimeEquals(aPadded, bPadded)
               && aBytes.Length == bBytes.Length;
    }

    static bool IsValidZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static byte[]? ExtractFromMultipart(byte[] body, string contentType)
    {
        // Parse boundary from Content-Type header
        const string boundaryPrefix = "boundary=";
        var idx = contentType.IndexOf(boundaryPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var boundary = contentType[(idx + boundaryPrefix.Length)..].Trim().Trim('"');

        var separator = Encoding.ASCII.GetBytes($"--{boundary}");
        var parts = SplitBytes(body, separator);

        foreach (var part in parts)
        {
            if (part.AsSpan().IndexOf("filename="u8) < 0)
                continue;

            // Find the double CRLF separating headers from body
            var headerEnd = IndexOf(part, "\r\n\r\n"u8);
            if (headerEnd < 0) continue;

            var payload = part[(headerEnd + 4)..];
            // Strip trailing CRLF
            if (payload.Length >= 2 && payload[^2] == '\r' && payload[^1] == '\n')
                payload = payload[..^2];

            return payload;
        }

        return null;
    }

    static List<byte[]> SplitBytes(byte[] source, byte[] separator)
    {
        var result = new List<byte[]>();
        int start = 0;
        while (true)
        {
            int pos = IndexOf(source.AsSpan()[start..], separator);
            if (pos < 0)
            {
                result.Add(source[start..]);
                break;
            }
            result.Add(source[start..(start + pos)]);
            start += pos + separator.Length;
        }
        return result;
    }

    static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (int i = 0; i <= source.Length - value.Length; i++)
        {
            if (source[i..].StartsWith(value))
                return i;
        }
        return -1;
    }

    static void RunSystemctl(string command, string service)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"{command} {service}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Ignore: no systemd, no permissions, timeout, etc.
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
