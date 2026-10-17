using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Registry.Models;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Gets the ACP registry index over HTTP.</summary>
/// <remarks>
/// A supplied <see cref="HttpClient"/> remains owned by the caller and is disposed only by its owner.
/// When none is supplied, this client creates one and disposes it. Prefer supplying a shared instance
/// (for example from <c>IHttpClientFactory</c>) so that registry clients do not each create their own
/// connection pool.
/// </remarks>
public sealed class RegistryClient : IRegistryClient
{
    /// <summary>Gets the default latest ACP agent registry index URI.</summary>
    public static Uri DefaultIndexUri { get; } = new("https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json");

    private readonly HttpClient _httpClient;
    private readonly Uri _indexUri;
    private readonly bool _ownsHttpClient;

    /// <summary>Creates a registry client, optionally using a caller-managed HTTP client.</summary>
    public RegistryClient(HttpClient? httpClient = null, Uri? indexUri = null)
    {
        _indexUri = indexUri ?? DefaultIndexUri;
        if (!_indexUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The registry index URI must be absolute.", nameof(indexUri));
        }

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<RegistryIndex> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            _indexUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(
                   stream,
                   RegistryJsonContext.Default.RegistryIndex,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException("The ACP registry returned an empty index document.");
    }
}
