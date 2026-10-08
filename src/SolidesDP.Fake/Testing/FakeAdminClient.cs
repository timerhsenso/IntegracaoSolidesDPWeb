using System.Text.Json;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;

namespace SolidesDP.Fake.Testing;

/// <summary>
/// Cliente tipado dos endpoints administrativos (<c>/_fake/*</c>) do fake. Embrulha um <see cref="HttpClient"/> ja
/// apontado para o fake (ex.: <c>factory.CreateClient()</c>); os endpoints administrativos nao exigem autenticacao.
/// </summary>
/// <param name="http">Cliente HTTP apontado para o fake.</param>
public sealed class FakeAdminClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = FakeJson.Options;

    /// <summary>Recarrega o seed, limpa diario e falhas e restaura o comportamento padrao da configuracao.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Post, "/_fake/reset", cancellationToken);

    /// <summary>Estado completo (tipado).</summary>
    public async Task<FakeState> GetStateAsync(CancellationToken cancellationToken = default) =>
        await Get<FakeState>("/_fake/state", cancellationToken);

    /// <summary>Estado completo como JSON cru (util para inspecionar <c>fields</c> e historicos livremente).</summary>
    public async Task<JsonDocument> GetStateDocumentAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri("/_fake/state", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>Diario de requests nao-admin, em ordem de chegada.</summary>
    public async Task<IReadOnlyList<RecordedRequest>> GetRequestsAsync(CancellationToken cancellationToken = default) =>
        await Get<List<RecordedRequest>>("/_fake/requests", cancellationToken);

    /// <summary>Limpa o diario de requests.</summary>
    public async Task ClearRequestsAsync(CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Delete, "/_fake/requests", cancellationToken);

    /// <summary>Comportamento corrente.</summary>
    public async Task<FakeBehavior> GetBehaviorAsync(CancellationToken cancellationToken = default) =>
        await Get<FakeBehavior>("/_fake/behavior", cancellationToken);

    /// <summary>Substitui o comportamento (propriedades omitidas voltam ao padrao).</summary>
    public async Task SetBehaviorAsync(FakeBehavior behavior, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync(new Uri("/_fake/behavior", UriKind.Relative), behavior, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Le o comportamento, aplica <paramref name="change"/> e o grava de volta.</summary>
    public async Task<FakeBehavior> UpdateBehaviorAsync(Action<FakeBehavior> change, CancellationToken cancellationToken = default)
    {
        var behavior = await GetBehaviorAsync(cancellationToken);
        change(behavior);
        await SetBehaviorAsync(behavior, cancellationToken);
        return behavior;
    }

    /// <summary>Cadastra uma regra de falha.</summary>
    public async Task AddFaultAsync(FaultRule rule, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri("/_fake/faults", UriKind.Relative), rule, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Regras de falha ainda ativas, com os consumos restantes.</summary>
    public async Task<IReadOnlyList<ActiveFaultRule>> GetFaultsAsync(CancellationToken cancellationToken = default) =>
        await Get<List<ActiveFaultRule>>("/_fake/faults", cancellationToken);

    /// <summary>Remove todas as regras de falha.</summary>
    public async Task ClearFaultsAsync(CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Delete, "/_fake/faults", cancellationToken);

    private async Task<T> Get<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidOperationException($"Resposta vazia de {path}.");
    }

    private async Task Send(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
