using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace S4MT.Mcp;

internal sealed class BearerTokenMiddleware
{
    private readonly RequestDelegate next;
    private readonly byte[] mcpToken;
    private readonly byte[] adminToken;

    public BearerTokenMiddleware(RequestDelegate next, HostOptions options)
    {
        this.next = next;
        mcpToken = LoadToken(options.HttpBearerTokenFile);
        adminToken = LoadToken(options.CacheAdminBearerTokenFile);
        if (CryptographicOperations.FixedTimeEquals(mcpToken, adminToken))
            throw new ArgumentException("The MCP and cache-admin bearer token files must contain different tokens.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context);
            return;
        }

        var supplied = Encoding.UTF8.GetBytes(authorization[7..]);
        var path = context.Request.Path.Value ?? string.Empty;
        var cacheAdminRequest = path.Equals("/api/cache", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/cache/", StringComparison.OrdinalIgnoreCase);
        var expected = cacheAdminRequest ? adminToken : mcpToken;
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            await RejectAsync(context);
            return;
        }

        await next(context);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync("Unauthorized.");
    }

    private static byte[] LoadToken(string? path)
    {
        var tokenText = File.ReadAllText(HostOptions.Absolute(path), Encoding.UTF8).TrimEnd('\r', '\n');
        if (tokenText.Length < 32 || tokenText.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Each HTTP bearer token file must contain at least 32 printable ASCII characters.");
        return Encoding.ASCII.GetBytes(tokenText);
    }
}
