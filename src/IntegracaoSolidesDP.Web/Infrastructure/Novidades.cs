using System.Text.RegularExpressions;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Uma seção do CHANGELOG.md: "## 1.0.2 — 12/10/2026" e os itens dela.</summary>
/// <param name="Titulo">O título inteiro, sem o "## ".</param>
/// <param name="Versao">O primeiro número de versão do título (ex.: 1.0.2); null se o título não tiver.</param>
/// <param name="Itens">As linhas "- ..." da seção.</param>
/// <param name="Textos">Os parágrafos soltos da seção.</param>
public sealed record VersaoNovidades(string Titulo, string? Versao, IReadOnlyList<string> Itens, IReadOnlyList<string> Textos);

/// <summary>
/// O CHANGELOG.md da raiz do repositório, embutido na dll da Web (ver o .csproj) e mostrado em Ajuda › Novidades.
/// Só o formato usado no arquivo: "## título", "- item" e parágrafos; o texto antes da primeira seção é ignorado.
/// </summary>
public static partial class Novidades
{
    /// <summary>Nome do recurso embutido.</summary>
    public const string Recurso = "IntegracaoSolidesDP.Web.CHANGELOG.md";

    private static readonly Lazy<IReadOnlyList<VersaoNovidades>> Embutidas = new(Carregar);

    public static IReadOnlyList<VersaoNovidades> Todas => Embutidas.Value;

    public static IReadOnlyList<VersaoNovidades> Interpretar(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var secoes = new List<VersaoNovidades>();
        string? titulo = null;
        var itens = new List<string>();
        var textos = new List<string>();

        void Fechar()
        {
            if (titulo is not null)
            {
                var versao = NumeroVersao().Match(titulo);
                secoes.Add(new VersaoNovidades(titulo, versao.Success ? versao.Value : null, itens.ToList(), textos.ToList()));
            }

            itens.Clear();
            textos.Clear();
        }

        foreach (var bruta in markdown.Split('\n'))
        {
            var linha = bruta.TrimEnd('\r').Trim();
            if (linha.StartsWith("## ", StringComparison.Ordinal))
            {
                Fechar();
                titulo = linha[3..].Trim();
            }
            else if (titulo is null || linha.Length == 0)
            {
                continue;
            }
            else if (linha.StartsWith("- ", StringComparison.Ordinal))
            {
                itens.Add(linha[2..].Trim());
            }
            else
            {
                textos.Add(linha);
            }
        }

        Fechar();
        return secoes;
    }

    private static IReadOnlyList<VersaoNovidades> Carregar()
    {
        using var stream = typeof(Novidades).Assembly.GetManifestResourceStream(Recurso);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        return Interpretar(reader.ReadToEnd());
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex NumeroVersao();
}
