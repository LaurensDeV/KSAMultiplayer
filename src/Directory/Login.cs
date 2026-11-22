using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace KSAMultiplayer.Directories;

/// <summary>
/// Logging in with Discord, for every server at once: the one place that holds the Discord
/// application's secret. A game's browser comes here carrying the public half of the game's key,
/// goes to Discord, and comes back here with a code, which is traded for who the player is; that
/// goes back to the game as a <see cref="DiscordCert"/> for its key, signed with this service's
/// key, which any server checks with <c>/v1/discord/key</c> and nothing else.
///
/// <para>Nothing is kept: not the code, not Discord's token, which is given back the moment it has
/// said who it belongs to, and not who logged in.</para>
///
/// <para>Off unless all four of KSAMP_DISCORD_CLIENT_ID, KSAMP_DISCORD_CLIENT_SECRET,
/// KSAMP_DISCORD_REDIRECT (this service's own <c>/v1/discord/callback</c>, as Discord knows it) and
/// KSAMP_DISCORD_SIGNING_KEY (an EC P-256 private key in PEM) are set.</para>
/// </summary>
public sealed class Login
{
    private const string Api = "https://discord.com/api/v10";

    private readonly string _clientId, _secret, _redirect;
    private readonly ECDsa? _key;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public Login(string clientId, string secret, string redirect, ECDsa? key)
    {
        (_clientId, _secret, _redirect, _key) = (clientId, secret, redirect, key);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Listings.Agent);
    }

    public static Login FromEnvironment()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
        ECDsa? key = null;
        if (Env("KSAMP_DISCORD_SIGNING_KEY") is { Length: > 0 } pem)
        {
            key = ECDsa.Create();
            // A PEM put in one line of an environment file has its line breaks written as \n.
            key.ImportFromPem(pem.Replace("\\n", "\n"));
        }
        return new Login(Env("KSAMP_DISCORD_CLIENT_ID"), Env("KSAMP_DISCORD_CLIENT_SECRET"), Env("KSAMP_DISCORD_REDIRECT"), key);
    }

    public bool On => Discord.IsSnowflake(_clientId) && _secret.Length > 0 && _redirect.Length > 0 && _key is not null;

    /// <summary>The key certificates are checked with, as SubjectPublicKeyInfo in base64.</summary>
    public string PublicKey => Convert.ToBase64String(_key!.ExportSubjectPublicKeyInfo());

    /// <summary>Discord's page asking the player to let this service know who they are: <c>identify</c> alone.</summary>
    public string Authorize(string state) =>
        "https://discord.com/oauth2/authorize?response_type=code"
        + $"&client_id={_clientId}&scope=identify&prompt=none"
        + $"&redirect_uri={Uri.EscapeDataString(_redirect)}&state={Uri.EscapeDataString(state)}";

    /// <summary>Back to the game on its own machine, with a certificate or why there is none, and its state.</summary>
    public static string BackToGame(string state, string? cert, string? error) =>
        $"http://127.0.0.1:{Discord.ReadState(state)?.Port ?? Discord.CallbackPort}/ksamp?"
        + (cert is not null ? $"cert={Uri.EscapeDataString(cert)}" : $"error={Uri.EscapeDataString(error ?? "unknown")}")
        + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>The certificate for the key the state carried, now its player has said who they are.</summary>
    public string Cert(Discord.LoginState state, string id, string name, DateTimeOffset now) =>
        new DiscordCert(id, name, state.K, now.ToUnixTimeSeconds() + Discord.CertSeconds).Sign(_key!);

    /// <summary>Who a code belongs to: traded for a token, the token for the user, and the token given back.</summary>
    public async Task<(string Id, string Name)> Identify(string code, CancellationToken cancel)
    {
        string token;
        using (HttpResponseMessage traded = await _http.PostAsync($"{Api}/oauth2/token", Form(
                   ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", _redirect)), cancel))
        {
            using JsonDocument body = await Json(traded, cancel);
            if (!traded.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Discord would not take the code: {Field(body, "error_description") ?? Field(body, "error") ?? $"HTTP {(int)traded.StatusCode}"}");
            }
            token = Field(body, "access_token") ?? throw new InvalidOperationException("Discord gave no token for the code");
        }
        try
        {
            using HttpRequestMessage ask = new(HttpMethod.Get, $"{Api}/users/@me");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage me = await _http.SendAsync(ask, cancel);
            using JsonDocument user = await Json(me, cancel);
            if (!me.IsSuccessStatusCode || Field(user, "id") is not { } id || !Discord.IsSnowflake(id))
            {
                throw new InvalidOperationException($"Discord would not say who logged in: HTTP {(int)me.StatusCode}");
            }
            return (id, Field(user, "global_name") is { Length: > 0 } shown ? shown : Field(user, "username") ?? id);
        }
        finally
        {
            // Not waited on: who it was is known, and a token left to run out does no more than this one could.
            _ = Revoke(token);
        }
    }

    private async Task Revoke(string token)
    {
        try
        {
            using HttpResponseMessage _ = await _http.PostAsync($"{Api}/oauth2/token/revoke",
                Form(("token", token), ("token_type_hint", "access_token")), CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    private FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new([.. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)),
             new("client_id", _clientId), new("client_secret", _secret)]);

    private static async Task<JsonDocument> Json(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel), default, cancel);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? Field(JsonDocument doc, string name) =>
        doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>The three endpoints, answering 404 while the service is off so a directory without it is a directory and no more.</summary>
    public void Map(WebApplication app, Func<HttpContext, IPAddress?> address)
    {
        app.MapGet("/v1/discord/key", (HttpContext http) =>
        {
            if (!On) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            // Where the asker was seen from: a server takes a proof only for an address that leads to
            // itself, and behind a router or in a container it cannot see its own.
            return Results.Ok(new { key = PublicKey, you = address(http)?.ToString() });
        }).RequireRateLimiting("reading");

        app.MapGet("/v1/discord/login", (string? state) =>
        {
            if (!On) return Results.NotFound();
            return state is not null && Discord.ReadState(state) is not null
                ? Results.Redirect(Authorize(state))
                : Results.BadRequest("Start the login from the Multiplayer window in the game.");
        }).RequireRateLimiting("reading");

        app.MapGet("/v1/discord/callback", async (string? code, string? state, string? error, CancellationToken cancel) =>
        {
            if (!On) return Results.NotFound();
            if (state is null || Discord.ReadState(state) is not { } asked) return Results.BadRequest("Start the login from the Multiplayer window in the game.");
            if (error is not null || code is null) return Results.Redirect(BackToGame(state, null, error ?? "Discord sent no code"));
            try
            {
                (string id, string name) = await Identify(code, cancel);
                app.Logger.LogInformation("a Discord login");
                return Results.Redirect(BackToGame(state, Cert(asked, id, name, DateTimeOffset.UtcNow), null));
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                app.Logger.LogWarning("a Discord login failed: {Why}", e.Message);
                return Results.Redirect(BackToGame(state, null, e.Message));
            }
        }).RequireRateLimiting("reading");
    }
}
