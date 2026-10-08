using System.Text.Json.Nodes;

namespace SolidesDP.Fake.Domain;

/// <summary>Entrada do diario de requests (<c>GET /_fake/requests</c>).</summary>
public sealed record RecordedRequest
{
    /// <summary>Numero de ordem (1, 2, ...) atribuido na chegada do request.</summary>
    public long Seq { get; init; }

    public string Method { get; init; } = "";

    /// <summary>Caminho sem query string.</summary>
    public string Path { get; init; } = "";

    /// <summary>Query string crua, sem o <c>?</c> inicial (nula quando nao ha).</summary>
    public string? Query { get; init; }

    /// <summary>Corpo: o JSON parseado quando valido, senao a string crua; nulo quando vazio.</summary>
    public JsonNode? Body { get; init; }

    /// <summary>Status HTTP produzido pelo fake (mesmo que a conexao tenha sido derrubada depois).</summary>
    public int Status { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>Tipo da falha injetada neste request (<c>Status</c>, <c>Delay</c>, ...), quando houve.</summary>
    public string? Fault { get; init; }
}
