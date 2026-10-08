using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace IntegracaoSolidesDP.Worker.Api;

public interface ISolidesDpClient
{
    Task<ApiResult<string>> TestAsync(CancellationToken ct);

    Task<ApiResult<IReadOnlyList<CompanyDto>>> GetCompaniesAsync(CancellationToken ct);

    Task<ApiResult<CompanyDto>> CreateCompanyAsync(CompanyRequest request, CancellationToken ct);

    Task<ApiResult<IReadOnlyList<WorkScheduleDto>>> GetWorkSchedulesAsync(CancellationToken ct);

    Task<ApiResult<IReadOnlyList<PunchRuleDto>>> GetPunchRulesAsync(CancellationToken ct);

    Task<ApiResult<IReadOnlyList<AdjustmentReasonDto>>> GetAdjustmentReasonsAsync(CancellationToken ct);

    Task<ApiResult<JobRoleDto>> FindJobRoleAsync(string externalId, CancellationToken ct);

    Task<ApiResult<JobRoleDto>> RegisterJobRoleAsync(JobRoleRequest request, CancellationToken ct);

    Task<ApiResult<WorkplaceDto>> FindWorkplaceAsync(string externalId, CancellationToken ct);

    Task<ApiResult<WorkplaceDto>> RegisterWorkplaceAsync(WorkplaceRequest request, CancellationToken ct);

    Task<ApiResult<EmployeeDto>> FindEmployeeAsync(string externalId, CancellationToken ct);

    Task<ApiResult<EmployeeDto>> FindEmployeeByIdAsync(long tangerinoId, CancellationToken ct);

    Task<ApiResult<EmployeeDto>> RegisterEmployeeAsync(EmployeeRequest request, CancellationToken ct);

    Task<ApiResult<JsonElement?>> DismissEmployeeAsync(DismissRequest request, CancellationToken ct);

    Task<ApiResult<AdjustmentRecordDto>> RegisterAdjustmentAsync(AdjustmentRegisterRequest request, CancellationToken ct);

    Task<ApiResult<AdjustmentRecordDto>> UpdateAdjustmentAsync(long id, AdjustmentUpdateRequest request, CancellationToken ct);

    Task<ApiResult<IReadOnlyList<AdjustmentRecordDto>>> FindAdjustmentsAsync(long employeeId, long adjustmentReasonId, CancellationToken ct);
}

/// <summary>
/// Cliente do Sólides DP. Usa dois HttpClients:
/// <list type="bullet">
/// <item><see cref="RetryClientName"/>: leituras e escritas idempotentes (upsert por externalId), com retry.</item>
/// <item><see cref="NoRetryClientName"/>: criações sem chave idempotente (cargo, férias). Um retry depois de
/// um timeout poderia duplicar o registro; o chamador reconcilia em vez de reenviar.</item>
/// </list>
/// </summary>
public sealed class SolidesDpClient(IHttpClientFactory factory, SolidesDpClientSettings settings) : ISolidesDpClient
{
    public const string RetryClientName = "SolidesDP";
    public const string NoRetryClientName = "SolidesDP.NoRetry";
    private const int MaxPages = 200;
    private const int PageSize = 100;

    public async Task<ApiResult<string>> TestAsync(CancellationToken ct)
    {
        var result = await SendAsync(RetryClientName, HttpMethod.Get, "test", null, ct);
        return result.Outcome == ApiOutcome.Success
            ? ApiResult<string>.Ok(result.Value?.ToString() ?? string.Empty, result.HttpStatus)
            : result.As<string>();
    }

    public Task<ApiResult<IReadOnlyList<CompanyDto>>> GetCompaniesAsync(CancellationToken ct) =>
        GetAllPagesAsync<CompanyDto>("companies", "pageNumber", "pageSize", static e => e, ct);

    public Task<ApiResult<CompanyDto>> CreateCompanyAsync(CompanyRequest request, CancellationToken ct) =>
        SendAsync<CompanyDto>(NoRetryClientName, HttpMethod.Post, "companies", request, ct);

    public Task<ApiResult<IReadOnlyList<WorkScheduleDto>>> GetWorkSchedulesAsync(CancellationToken ct) =>
        GetAllPagesAsync<WorkScheduleDto>("work-schedule", "page", "size", static e => e, ct);

    public Task<ApiResult<IReadOnlyList<PunchRuleDto>>> GetPunchRulesAsync(CancellationToken ct) =>
        // GET /v2/punch-rule devolve BaseItemDTO«Page«...»»: a página está em "item".
        GetAllPagesAsync<PunchRuleDto>(
            "v2/punch-rule", "pageNumber", "pageSize",
            static e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("item", out var item) ? item : e,
            ct);

    public Task<ApiResult<IReadOnlyList<AdjustmentReasonDto>>> GetAdjustmentReasonsAsync(CancellationToken ct) =>
        GetAllPagesAsync<AdjustmentReasonDto>("adjustment-reason/find-all", "page", "size", static e => e, ct);

    public Task<ApiResult<JobRoleDto>> FindJobRoleAsync(string externalId, CancellationToken ct) =>
        SendAsync<JobRoleDto>(RetryClientName, HttpMethod.Get, $"job-role/find?externalId={Uri.EscapeDataString(externalId)}", null, ct);

    public Task<ApiResult<JobRoleDto>> RegisterJobRoleAsync(JobRoleRequest request, CancellationToken ct) =>
        // Sem allowUpdate no Swagger: reenviar o mesmo externalId pode duplicar o cargo. Sem retry.
        SendAsync<JobRoleDto>(NoRetryClientName, HttpMethod.Post, "job-role/register", request, ct);

    public Task<ApiResult<WorkplaceDto>> FindWorkplaceAsync(string externalId, CancellationToken ct) =>
        SendAsync<WorkplaceDto>(RetryClientName, HttpMethod.Get, $"workplace/find?externalId={Uri.EscapeDataString(externalId)}", null, ct);

    public Task<ApiResult<WorkplaceDto>> RegisterWorkplaceAsync(WorkplaceRequest request, CancellationToken ct) =>
        SendAsync<WorkplaceDto>(RetryClientName, HttpMethod.Post, "workplace/register?allowUpdate=true", request, ct);

    public Task<ApiResult<EmployeeDto>> FindEmployeeAsync(string externalId, CancellationToken ct) =>
        SendAsync<EmployeeDto>(RetryClientName, HttpMethod.Get, $"employee/find?externalId={Uri.EscapeDataString(externalId)}", null, ct);

    public Task<ApiResult<EmployeeDto>> FindEmployeeByIdAsync(long tangerinoId, CancellationToken ct) =>
        SendAsync<EmployeeDto>(RetryClientName, HttpMethod.Get, FormattableString.Invariant($"employee/find?tangerinoId={tangerinoId}"), null, ct);

    public Task<ApiResult<EmployeeDto>> RegisterEmployeeAsync(EmployeeRequest request, CancellationToken ct) =>
        // allowUpdate=true torna o POST um upsert por externalId: o retry é seguro.
        SendAsync<EmployeeDto>(
            RetryClientName,
            HttpMethod.Post,
            $"employee/register?allowUpdate=true&skipUnifiedSync={(settings.SkipUnifiedSync ? "true" : "false")}",
            request,
            ct);

    public Task<ApiResult<JsonElement?>> DismissEmployeeAsync(DismissRequest request, CancellationToken ct) =>
        SendAsync(RetryClientName, HttpMethod.Post, "employee/dismiss", request, ct);

    public async Task<ApiResult<AdjustmentRecordDto>> RegisterAdjustmentAsync(AdjustmentRegisterRequest request, CancellationToken ct)
    {
        var result = await SendAsync(NoRetryClientName, HttpMethod.Post, "adjustment/register/1.1", request, ct);
        return FromLaunchResponse(result);
    }

    public async Task<ApiResult<AdjustmentRecordDto>> UpdateAdjustmentAsync(long id, AdjustmentUpdateRequest request, CancellationToken ct)
    {
        var result = await SendAsync(RetryClientName, HttpMethod.Put, FormattableString.Invariant($"adjustment/update/{id}"), request, ct);
        return FromLaunchResponse(result);
    }

    public Task<ApiResult<IReadOnlyList<AdjustmentRecordDto>>> FindAdjustmentsAsync(long employeeId, long adjustmentReasonId, CancellationToken ct) =>
        GetAllPagesAsync<AdjustmentRecordDto>(
            FormattableString.Invariant($"adjustment/find-all?employeeId={employeeId}&adjustmentReasonId={adjustmentReasonId}&ignoreExcluded=true"),
            "page", "size", static e => e, ct);

    /// <summary>AdjustmentLaunch(Only)ResponseDTO: o registro criado/alterado vem em "entity".</summary>
    private static ApiResult<AdjustmentRecordDto> FromLaunchResponse(ApiResult<JsonElement?> result)
    {
        if (!result.IsSuccess)
        {
            return result.As<AdjustmentRecordDto>();
        }

        var entity = result.Value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("entity", out var e) ? e : result.Value;
        var record = entity is { ValueKind: JsonValueKind.Object } r ? r.Deserialize<AdjustmentRecordDto>(SolidesDpJson.Options) : null;
        return record is { Id: > 0 }
            ? ApiResult<AdjustmentRecordDto>.Ok(record, result.HttpStatus)
            : new ApiResult<AdjustmentRecordDto>(ApiOutcome.Rejected, null, result.HttpStatus, "Resposta sem entity.id");
    }

    private async Task<ApiResult<T>> SendAsync<T>(string clientName, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var result = await SendAsync(clientName, method, path, body, ct);
        if (!result.IsSuccess)
        {
            return result.As<T>();
        }

        try
        {
            var value = result.Value is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json
                ? json.Deserialize<T>(SolidesDpJson.Options)
                : default;
            return value is null
                ? new ApiResult<T>(ApiOutcome.Rejected, default, result.HttpStatus, "Resposta vazia ou inesperada")
                : ApiResult<T>.Ok(value, result.HttpStatus);
        }
        catch (JsonException ex)
        {
            return new ApiResult<T>(ApiOutcome.Rejected, default, result.HttpStatus, $"Resposta inesperada: {ex.Message}");
        }
    }

    private async Task<ApiResult<JsonElement?>> SendAsync(string clientName, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var client = factory.CreateClient(clientName);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: SolidesDpJson.Options);
        }

        try
        {
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var (outcome, message, payload) = ApiResponseClassifier.Classify(response.StatusCode, text);
            return new ApiResult<JsonElement?>(outcome, payload, (int)response.StatusCode, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or IOException
                                   // Conexão abortada pelo servidor também chega como cancelamento, mas não foi o nosso token.
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new ApiResult<JsonElement?>(ApiOutcome.TransportError, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<ApiResult<IReadOnlyList<T>>> GetAllPagesAsync<T>(
        string path, string pageParam, string sizeParam, Func<JsonElement, JsonElement> unwrap, CancellationToken ct)
    {
        var all = new List<T>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        for (var page = 0; page < MaxPages; page++)
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"{path}{separator}{pageParam}={page}&{sizeParam}={PageSize}");
            var result = await SendAsync(RetryClientName, HttpMethod.Get, url, null, ct);
            if (!result.IsSuccess)
            {
                return result.As<IReadOnlyList<T>>();
            }

            if (result.Value is not { } json)
            {
                break;
            }

            var pageJson = unwrap(json);
            if (pageJson.ValueKind == JsonValueKind.Array)
            {
                all.AddRange(pageJson.Deserialize<List<T>>(SolidesDpJson.Options) ?? []);
                break;
            }

            var dto = pageJson.Deserialize<PageDto<T>>(SolidesDpJson.Options);
            if (dto is null || dto.Content.Count == 0)
            {
                break;
            }

            all.AddRange(dto.Content);
            var isLast = dto.Last == true
                         || (dto.TotalPages is { } total && page + 1 >= total)
                         || dto.Content.Count < PageSize;
            if (isLast)
            {
                break;
            }
        }

        return ApiResult<IReadOnlyList<T>>.Ok(all, 200);
    }
}

/// <summary>Configurações que o cliente precisa em tempo de chamada.</summary>
public sealed record SolidesDpClientSettings(bool SkipUnifiedSync);
