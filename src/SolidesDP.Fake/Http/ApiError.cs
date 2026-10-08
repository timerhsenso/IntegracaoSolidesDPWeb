namespace SolidesDP.Fake.Http;

/// <summary>Codigos de erro estaveis do fake (aparecem em <c>body.error</c> e no header <c>X-Fake-Error-Code</c>).</summary>
internal static class ErrorCodes
{
    public const string InvalidJson = "invalid_json";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string UnknownField = "unknown_field";
    public const string InvalidValue = "invalid_value";
    public const string RequiredField = "required_field";
    public const string InvalidReference = "invalid_reference";
    public const string InvalidDateRange = "invalid_date_range";
    public const string NotFound = "not_found";
    public const string AlreadyExists = "already_exists";
    public const string DuplicateCpf = "duplicate_cpf";
    public const string DuplicateCnpj = "duplicate_cnpj";
    public const string DuplicateExternalId = "duplicate_external_id";
    public const string EmployeeFired = "employee_fired";
    public const string AlreadyFired = "already_fired";
    public const string Overlap = "overlap";
    public const string InjectedFault = "injected_fault";
}

/// <summary>Formato do erro que o endpoint devolve no <see cref="Configuration.ErrorStyle.ResponseEntity"/>.</summary>
internal enum ErrorShape
{
    /// <summary>Sem formato proprio: sempre HTTP 4xx com corpo no estilo Spring.</summary>
    Spring,

    /// <summary>Schema 200 <c>ResponseEntity</c>: HTTP 200 com <c>{body, statusCode, statusCodeValue}</c>.</summary>
    ResponseEntity,

    /// <summary>Schema 200 <c>AdjustmentLaunch*ResponseDTO</c>: HTTP 200 com <c>{registered:false, message}</c>.</summary>
    Launch,
}

/// <summary>Erro de validacao/negocio. <paramref name="Transport"/> = falha anterior ao controller (JSON/Content-Type/parametros).</summary>
internal sealed record ApiError(string Code, string Message, int Status, bool Transport = false)
{
    public static ApiError BadRequest(string code, string message) => new(code, message, StatusCodes.Status400BadRequest);

    public static ApiError NotFound(string message) => new(ErrorCodes.NotFound, message, StatusCodes.Status404NotFound);

    public static ApiError Conflict(string code, string message) => new(code, message, StatusCodes.Status409Conflict);

    public static ApiError TransportFailure(string code, string message, int status = StatusCodes.Status400BadRequest) =>
        new(code, message, status, Transport: true);
}

/// <summary>Excecao usada pelos servicos para abortar com um <see cref="ApiError"/>.</summary>
internal sealed class ApiException(ApiError error) : Exception(error.Message)
{
    public ApiError Error { get; } = error;
}
