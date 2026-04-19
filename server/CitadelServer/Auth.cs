using System.Security.Cryptography;
using System.Text;

namespace CitadelServer;

/// <summary>
/// Auth protocol v2 — HMAC-SHA256 over (timestamp || LF || context || LF || body).
/// Timestamp is unix-seconds in ASCII digits; context identifies the request
/// (profile name for /deploy, "METHOD path" for everything else). The timestamp
/// must be within ±300 s of server time.
/// </summary>
public static class Auth
{
    public const string Protocol = "v2";
    public const int MaxSkewSeconds = 300;
    public static readonly byte[] Lf = [(byte)'\n'];

    public enum Result
    {
        Ok,
        MissingProtocol,
        WrongProtocol,
        MissingTimestamp,
        InvalidTimestamp,
        TimestampSkew,
        MissingSignature,
        InvalidSignature,
        SignatureMismatch,
    }

    public static string Describe(Result r) => r switch
    {
        Result.Ok => "ok",
        Result.MissingProtocol => "missing X-Protocol header (upgrade client)",
        Result.WrongProtocol => "unsupported protocol version; upgrade client",
        Result.MissingTimestamp => "missing X-Timestamp header",
        Result.InvalidTimestamp => "invalid X-Timestamp header (must be unix seconds)",
        Result.TimestampSkew => "timestamp out of range (check client/server clock)",
        Result.MissingSignature => "missing X-Signature header",
        Result.InvalidSignature => "invalid X-Signature header (must be hex)",
        Result.SignatureMismatch => "signature mismatch",
        _ => "unauthorized",
    };

    public static int StatusCode(Result r) => r switch
    {
        Result.MissingProtocol or Result.WrongProtocol => 400,
        _ => 401,
    };

    public readonly record struct Headers(long Timestamp, byte[] Signature);

    /// <summary>
    /// Validates protocol + timestamp + signature format. On Ok, returns the parsed
    /// timestamp + signature bytes. Does NOT compute or compare the HMAC — caller
    /// does that after feeding the body through IncrementalHash.
    /// </summary>
    public static (Result result, Headers headers) ParseHeaders(HttpContext ctx)
    {
        var protocol = ctx.Request.Headers["X-Protocol"].ToString();
        if (string.IsNullOrEmpty(protocol)) return (Result.MissingProtocol, default);
        if (protocol != Protocol) return (Result.WrongProtocol, default);

        var tsStr = ctx.Request.Headers["X-Timestamp"].ToString();
        if (string.IsNullOrEmpty(tsStr)) return (Result.MissingTimestamp, default);
        if (!long.TryParse(tsStr, out var ts)) return (Result.InvalidTimestamp, default);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > MaxSkewSeconds) return (Result.TimestampSkew, default);

        var sigHeader = ctx.Request.Headers["X-Signature"].ToString();
        if (string.IsNullOrEmpty(sigHeader)) return (Result.MissingSignature, default);

        byte[] sigBytes;
        try { sigBytes = Convert.FromHexString(sigHeader); }
        catch (FormatException) { return (Result.InvalidSignature, default); }

        return (Result.Ok, new Headers(ts, sigBytes));
    }

    /// <summary>
    /// Creates a configured IncrementalHash seeded with the v2 header prefix
    /// (timestamp + LF + context + LF). Caller appends body bytes and then
    /// calls <see cref="FinalizeAndCompare"/>.
    /// </summary>
    public static IncrementalHash BeginHmac(string token, long timestamp, string context)
    {
        var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token));
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp.ToString()));
        hmac.AppendData(Lf);
        hmac.AppendData(Encoding.UTF8.GetBytes(context));
        hmac.AppendData(Lf);
        return hmac;
    }

    /// <summary>
    /// Computes the final HMAC and compares against the client-supplied signature in constant time.
    /// Disposes the IncrementalHash.
    /// </summary>
    public static Result FinalizeAndCompare(IncrementalHash hmac, byte[] expected)
    {
        using var _ = hmac;
        var actual = hmac.GetHashAndReset();
        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? Result.Ok
            : Result.SignatureMismatch;
    }

    /// <summary>
    /// Full verification for empty-body requests (e.g. GET /profiles).
    /// </summary>
    public static Result VerifyNoBody(HttpContext ctx, string token, string context)
    {
        var (result, headers) = ParseHeaders(ctx);
        if (result != Result.Ok) return result;
        using var hmac = BeginHmac(token, headers.Timestamp, context);
        return FinalizeAndCompare(hmac, headers.Signature);
    }

    /// <summary>
    /// Full verification for in-memory-body requests. Keeps the v2 semantics identical
    /// to the streamed deploy path.
    /// </summary>
    public static Result VerifyWithBody(HttpContext ctx, string token, string context, ReadOnlySpan<byte> body)
    {
        var (result, headers) = ParseHeaders(ctx);
        if (result != Result.Ok) return result;
        using var hmac = BeginHmac(token, headers.Timestamp, context);
        hmac.AppendData(body);
        return FinalizeAndCompare(hmac, headers.Signature);
    }
}
