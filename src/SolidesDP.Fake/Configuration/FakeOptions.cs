namespace SolidesDP.Fake.Configuration;

/// <summary>Opcoes da secao <c>Fake</c> (appsettings, variaveis de ambiente ou config do WebApplicationFactory).</summary>
public sealed class FakeOptions
{
    /// <summary>Nome da secao de configuracao.</summary>
    public const string SectionName = "Fake";

    /// <summary>Token usado quando <see cref="Tokens"/> nao e configurado.</summary>
    public const string DefaultToken = "fake-token";

    /// <summary>
    /// Tokens validos. Fica nulo por padrao (e nao <c>["fake-token"]</c>) porque o binder de configuracao
    /// <em>acrescenta</em> elementos a arrays ja preenchidos em vez de substitui-los.
    /// </summary>
    public string[]? Tokens { get; set; }

    /// <summary>Caminho de um JSON de seed; vazio usa o recurso embutido <c>fake-seed.json</c>.</summary>
    public string? SeedFile { get; set; }

    /// <summary>Perfil de comportamento inicial (restaurado por <c>POST /_fake/reset</c>).</summary>
    public FakeBehavior Behavior { get; set; } = new();

    /// <summary>Tokens efetivos, aplicando o padrao.</summary>
    public IReadOnlyList<string> EffectiveTokens => Tokens is { Length: > 0 } ? Tokens : [DefaultToken];
}
