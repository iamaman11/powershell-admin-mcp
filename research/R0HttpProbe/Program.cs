using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// Self-contained CI fixture. No external IdP, network exposure, real users,
// administrative API, or production use of this ephemeral symmetric signing key.
const string issuer = "https://r0-fixture.invalid";
const string audience = "r0-fixture";
var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = issuer,
        ValidateAudience = true, ValidAudience = audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = key,
        ValidateLifetime = true, RequireExpirationTime = true,
        RequireSignedTokens = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
});
builder.Services.AddAuthorization();
builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<IdentityProbeTools>();
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization();
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(12) };
    string Token(string subject, string? aud = null, bool expired = false, bool forged = false)
    {
        var signingKey = forged ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) : key;
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer, aud ?? audience, [new Claim("sub", subject)], now.AddMinutes(-10),
            expired ? now.AddMinutes(-5) : now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
    }
    var alpha = Token("fixture-alpha");
    var beta = Token("fixture-beta");
    var gamma = Token("fixture-gamma");
    var meta = new Dictionary<string, object>
    {
        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
        ["io.modelcontextprotocol/clientCapabilities"] = new { },
        ["io.modelcontextprotocol/clientInfo"] = new { name = "r0-http-fixture", version = "1" }
    };
    async Task<(HttpStatusCode Status, JsonElement Body, bool Session)> Request(
        string? token, string method, object? args = null, string? forgedOwnerHeader = null)
    {
        var parameters = new Dictionary<string, object> { ["_meta"] = meta };
        if (method == "tools/call")
        {
            parameters["name"] = "probe_identity";
            parameters["arguments"] = args ?? new { };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forgedOwnerHeader is not null) request.Headers.Add("X-User-Id", forgedOwnerHeader);
        request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        using var response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = default;
        if (response.IsSuccessStatusCode)
        {
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                text = text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].Trim()).Last();
            using var doc = JsonDocument.Parse(text);
            body = doc.RootElement.Clone();
        }
        return (response.StatusCode, body, response.Headers.Contains("Mcp-Session-Id"));
    }
    static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    foreach (var (token, label) in new (string?, string)[]
    {
        (null, "anonymous"), (Token("fixture-alpha", forged: true), "invalid_signature"),
        (Token("fixture-alpha", expired: true), "expired"), (Token("fixture-alpha", aud: "wrong"), "wrong_audience")
    })
    {
        var deny = await Request(token, "tools/call");
        Check(deny.Status == HttpStatusCode.Unauthorized, "http.deny." + label);
    }
    var discovery = await Request(alpha, "server/discover");
    Check(discovery.Status == HttpStatusCode.OK && !discovery.Body.TryGetProperty("error", out _) && !discovery.Session,
        "http.authenticated_stateless_discovery");
    var list = await Request(alpha, "tools/list");
    var schema = list.Body.GetProperty("result").GetProperty("tools")[0].GetProperty("inputSchema");
    Check(!schema.GetProperty("properties").TryGetProperty("user", out _), "http.principal_not_exposed_in_schema");

    var tokens = new[] { (alpha, "fixture-alpha"), (beta, "fixture-beta"), (gamma, "fixture-gamma"), (alpha, "fixture-alpha") };
    var results = await Task.WhenAll(tokens.Select(async item =>
    {
        var call = await Request(item.Item1, "tools/call", new { requestedOwner = "fixture-alpha" }, "fixture-alpha");
        var result = call.Body.GetProperty("result").GetProperty("structuredContent");
        return (item.Item2, Subject: result.GetProperty("subject").GetString(), Own: result.GetProperty("isOwner").GetBoolean(), call.Session);
    }));
    Check(results.All(x => x.Item2 == x.Subject && !x.Session), "http.concurrent_verified_subjects");
    Check(results.All(x => x.Own == (x.Item2 == "fixture-alpha")), "http.owner_scope_and_forged_header_ignored");
    Console.WriteLine("R0_HTTP_IDENTITY=PASS (synthetic signed JWTs; no external IdP or tunnel)");
}
finally { await app.StopAsync(); }

[McpServerToolType]
public sealed class IdentityProbeTools
{
    [McpServerTool(Name = "probe_identity", UseStructuredContent = true), Description("Verify request principal, using a synthetic owner.")]
    public static IdentityResult Verify(ClaimsPrincipal user, string requestedOwner = "fixture-alpha")
    {
        if (user.Identity?.IsAuthenticated != true) throw new InvalidOperationException("Missing authenticated principal");
        string subject = user.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Missing subject");
        return new IdentityResult(subject, StringComparer.Ordinal.Equals(subject, requestedOwner));
    }
}
public sealed record IdentityResult(string Subject, bool IsOwner);
