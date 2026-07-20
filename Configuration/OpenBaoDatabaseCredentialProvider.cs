using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Coflnet.Security.OpenBao;

/// <summary>
/// Supplies the PostgreSQL/CockroachDB password from OpenBao at runtime.
///
/// The application keeps a stable username (so the Npgsql connection pool stays
/// intact) while OpenBao rotates the password. The password is read from a
/// OpenBao database secrets-engine endpoint (a static role by default) and is
/// refreshed periodically by Npgsql via <c>UsePeriodicPasswordProvider</c>.
///
/// When <c>OPENBAO__DB__ENABLED</c> is not set the whole feature is inert and
/// the caller keeps using the static <c>DB_CONNECTION</c> string (local dev,
/// appsettings, plain environment variables).
/// </summary>
internal sealed class OpenBaoDatabaseCredentialProvider
{
    private static readonly TimeSpan MaximumProviderPollInterval = TimeSpan.FromSeconds(5);
    private readonly OpenBaoDatabaseOptions options;
    private readonly SemaphoreSlim credentialRefreshLock = new(1, 1);
    private string? cachedPassword;
    private DateTimeOffset nextCredentialFetchUtc = DateTimeOffset.MinValue;

    private OpenBaoDatabaseCredentialProvider(OpenBaoDatabaseOptions options) => this.options = options;

    /// <summary>
    /// Builds an <see cref="NpgsqlDataSource"/> that fetches its password from
    /// OpenBao and refreshes it on the configured interval.
    /// </summary>
    /// <param name="baseConnectionString">
    /// Connection string containing everything except the password (host, port,
    /// database, username, SSL settings). A password present here is ignored.
    /// </param>
    public static NpgsqlDataSource BuildDataSource(string? baseConnectionString, OpenBaoDatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(baseConnectionString))
            throw new InvalidOperationException("DB_CONNECTION is required to build the OpenBao-backed data source.");

        options.Validate();
        var provider = new OpenBaoDatabaseCredentialProvider(options);

        // Strip any password from the base string; OpenBao supplies it.
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Password = null };
        if (options.MtlsEnabled)
        {
            // Keep password authentication during enrollment while presenting
            // a short-lived client certificate. VerifyFull also removes the
            // legacy Trust Server Certificate escape hatch.
            builder.SslMode = SslMode.VerifyFull;
            builder.Remove("Trust Server Certificate");
            builder.RootCertificate = null;
            builder.SslCertificate = null;
            builder.SslKey = null;
        }
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        if (options.MtlsEnabled)
        {
            // Npgsql invokes these callbacks for every new physical connection,
            // so cert-manager file rotations do not require a process restart.
            dataSourceBuilder.UseSslClientAuthenticationOptionsCallback(sslOptions =>
            {
                sslOptions.ClientCertificates = new X509CertificateCollection
                {
                    X509Certificate2.CreateFromPemFile(
                        options.ClientCertificatePath,
                        options.ClientKeyPath)
                };
            });
            dataSourceBuilder.UseRootCertificatesCallback(() =>
            {
                var certificates = new X509Certificate2Collection();
                certificates.ImportFromPemFile(options.RootCertificatePath);
                return certificates;
            });
        }
        dataSourceBuilder.UsePeriodicPasswordProvider(
            (_, ct) => provider.GetPasswordAsync(ct),
            ProviderPollInterval(options.RefreshInterval),
            ProviderPollInterval(options.FailureRefreshInterval));
        return dataSourceBuilder.Build();
    }

    // OpenBao returns the remaining static-role TTL with the current password.
    // Wake Npgsql frequently enough to observe the rotation boundary, but keep
    // returning the in-memory value until that TTL expires. This avoids both
    // the former 30-minute stale-password outage and continuous OpenBao logins.
    private async ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
    {
        await credentialRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (cachedPassword is not null && now < nextCredentialFetchUtc)
                return cachedPassword;

            var credential = await FetchCredentialAsync(cancellationToken).ConfigureAwait(false);
            cachedPassword = credential.Password;
            nextCredentialFetchUtc = DateTimeOffset.UtcNow.Add(credential.RefreshAfter);
            return cachedPassword;
        }
        finally
        {
            credentialRefreshLock.Release();
        }
    }

    private async ValueTask<(string Password, TimeSpan RefreshAfter)> FetchCredentialAsync(
        CancellationToken cancellationToken)
    {
        var jwt = await File.ReadAllTextAsync(options.TokenPath, cancellationToken).ConfigureAwait(false);
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(options.CACertPath))
        {
            if (!File.Exists(options.CACertPath))
                throw new FileNotFoundException("OpenBao CA certificate not found.", options.CACertPath);

            var caCert = X509CertificateLoader.LoadCertificateFromFile(options.CACertPath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                    return true;
                if (cert is null || chain is null)
                    return false;

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCert);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;
                return chain.Build(new X509Certificate2(cert));
            };
        }

        using var client = new HttpClient(handler) { BaseAddress = new Uri(options.Address.TrimEnd('/') + "/") };

        var loginPayload = JsonSerializer.Serialize(new { role = options.Role, jwt });
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/auth/{options.AuthPath.Trim('/')}/login")
        {
            Content = new StringContent(loginPayload, Encoding.UTF8, "application/json")
        };
        using var loginResponse = await client.SendAsync(loginRequest, cancellationToken).ConfigureAwait(false);
        loginResponse.EnsureSuccessStatusCode();

        using var loginDocument = JsonDocument.Parse(
            await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var token = loginDocument.RootElement.GetProperty("auth").GetProperty("client_token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("OpenBao login did not return a client token.");

        // static-creds/<role> (fixed user, rotated password) or creds/<role> (dynamic user).
        var endpoint = $"v1/{options.Mount.Trim('/')}/{options.CredentialEndpoint}/{options.Role}";
        using var credsRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        credsRequest.Headers.Add("X-Vault-Token", token);
        credsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var credsResponse = await client.SendAsync(credsRequest, cancellationToken).ConfigureAwait(false);
        credsResponse.EnsureSuccessStatusCode();

        using var credsDocument = JsonDocument.Parse(
            await credsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var data = credsDocument.RootElement.GetProperty("data");
        if (!data.TryGetProperty("password", out var password) || password.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"OpenBao response from {endpoint} did not contain a password.");

        return (password.GetString()!, CredentialRefreshDelay(data, options.RefreshInterval));
    }

    internal static TimeSpan ProviderPollInterval(TimeSpan configuredInterval) =>
        configuredInterval <= MaximumProviderPollInterval
            ? configuredInterval
            : MaximumProviderPollInterval;

    internal static TimeSpan CredentialRefreshDelay(JsonElement data, TimeSpan fallbackInterval)
    {
        if (data.TryGetProperty("ttl", out var ttl) &&
            ttl.ValueKind == JsonValueKind.Number &&
            ttl.TryGetInt64(out var ttlSeconds))
        {
            // A zero TTL means rotation is due but may still be in progress.
            // Retry on the next short provider poll instead of falling back to
            // the long legacy interval with a stale password.
            return TimeSpan.FromSeconds(Math.Max(1, ttlSeconds));
        }

        return fallbackInterval;
    }
}

internal sealed record OpenBaoDatabaseOptions
{
    public bool Enabled { get; init; }
    public string Address { get; init; } = "";
    public string AuthPath { get; init; } = "kubernetes";
    public string Mount { get; init; } = "database";
    public string Role { get; init; } = "";
    public string TokenPath { get; init; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    public string CACertPath { get; init; } = "";
    public bool MtlsEnabled { get; init; }
    public string ClientCertificatePath { get; init; } = "";
    public string ClientKeyPath { get; init; } = "";
    public string RootCertificatePath { get; init; } = "";

    /// <summary>"static" -&gt; static-creds (fixed user, rotated password); "dynamic" -&gt; creds.</summary>
    public string CredentialsKind { get; init; } = "static";
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan FailureRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    public string CredentialEndpoint =>
        string.Equals(CredentialsKind, "dynamic", StringComparison.OrdinalIgnoreCase) ? "creds" : "static-creds";

    public static OpenBaoDatabaseOptions FromEnvironment()
    {
        return new OpenBaoDatabaseOptions
        {
            Enabled = Bool("OPENBAO__DB__ENABLED", false),
            // Reuse the application's existing OpenBao coordinates where a
            // database-specific override is not provided.
            Address = Env("OPENBAO__DB__ADDR", Env("OPENBAO__ADDR")),
            AuthPath = Env("OPENBAO__DB__AUTH_PATH", Env("OPENBAO__AUTH_PATH", "kubernetes")),
            Mount = Env("OPENBAO__DB__MOUNT", "database"),
            Role = Env("OPENBAO__DB__ROLE"),
            TokenPath = Env("OPENBAO__DB__TOKEN_PATH",
                Env("OPENBAO__TOKEN_PATH", "/var/run/secrets/kubernetes.io/serviceaccount/token")),
            CACertPath = Env("OPENBAO__DB__CACERT", Env("OPENBAO__CACERT")),
            MtlsEnabled = Bool("OPENBAO__DB__MTLS__ENABLED", false),
            ClientCertificatePath = Env("OPENBAO__DB__MTLS__CLIENT_CERT_PATH"),
            ClientKeyPath = Env("OPENBAO__DB__MTLS__CLIENT_KEY_PATH"),
            RootCertificatePath = Env("OPENBAO__DB__MTLS__ROOT_CERT_PATH"),
            CredentialsKind = Env("OPENBAO__DB__CREDENTIALS_KIND", "static"),
            RefreshInterval = TimeSpan.FromSeconds(Int("OPENBAO__DB__REFRESH_SECONDS", 1800)),
            FailureRefreshInterval = TimeSpan.FromSeconds(Int("OPENBAO__DB__FAILURE_REFRESH_SECONDS", 10))
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Address)) throw new InvalidOperationException("OPENBAO__ADDR (or OPENBAO__DB__ADDR) is required for dynamic database credentials.");
        if (string.IsNullOrWhiteSpace(Role)) throw new InvalidOperationException("OPENBAO__DB__ROLE is required for dynamic database credentials.");
        if (!File.Exists(TokenPath)) throw new FileNotFoundException("Kubernetes service account token not found.", TokenPath);
        if (RefreshInterval <= TimeSpan.Zero) throw new InvalidOperationException("OPENBAO__DB__REFRESH_SECONDS must be positive.");
        if (FailureRefreshInterval <= TimeSpan.Zero) throw new InvalidOperationException("OPENBAO__DB__FAILURE_REFRESH_SECONDS must be positive.");
        if (MtlsEnabled)
        {
            RequireFile(ClientCertificatePath, "OPENBAO__DB__MTLS__CLIENT_CERT_PATH");
            RequireFile(ClientKeyPath, "OPENBAO__DB__MTLS__CLIENT_KEY_PATH");
            RequireFile(RootCertificatePath, "OPENBAO__DB__MTLS__ROOT_CERT_PATH");
        }
    }

    private static void RequireFile(string path, string variable)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{variable} is required when database mTLS is enabled.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"{variable} file not found.", path);
    }

    private static string Env(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static bool Bool(string key, bool fallback) => bool.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
    private static int Int(string key, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
}
