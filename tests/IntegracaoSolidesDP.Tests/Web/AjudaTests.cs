using System.Reflection;
using IntegracaoSolidesDP.Web.Controllers;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>O manual não pode ficar para trás do código: todo status, campo e tópico tem texto.</summary>
public sealed class AjudaTests
{
    [Fact]
    public void Every_status_shown_on_screen_is_explained_in_the_glossary()
    {
        var explicados = AjudaStatus.Grupos.SelectMany(g => g.Itens).Select(i => i.Status).ToHashSet();

        explicados.Should().BeEquivalentTo(Rotulos.Status.Keys, "todo status com rótulo precisa estar no glossário, e vice-versa");
        AjudaStatus.Grupos.SelectMany(g => g.Itens).Should().AllSatisfy(i => i.Significado.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Changelog_sections_items_and_paragraphs_are_read_ignoring_the_header()
    {
        var secoes = Novidades.Interpretar("""
            # Novidades
            Texto do cabeçalho, ignorado.

            ## 1.0.3 — 20/10/2026
            Ajustes da homologação.
            - Primeiro item.
            - Segundo item.

            ## 1.0.1 e anteriores
            - Item antigo.
            """);

        secoes.Should().HaveCount(2);
        secoes[0].Should().Match<VersaoNovidades>(s => s.Titulo == "1.0.3 — 20/10/2026" && s.Versao == "1.0.3");
        secoes[0].Itens.Should().Equal("Primeiro item.", "Segundo item.");
        secoes[0].Textos.Should().Equal("Ajustes da homologação.");
        secoes[1].Versao.Should().Be("1.0.1");
        secoes[1].Itens.Should().Equal("Item antigo.");
    }

    [Fact]
    public void The_changelog_goes_inside_the_web_and_the_newest_version_comes_first_with_items()
    {
        Novidades.Todas.Should().NotBeEmpty("o CHANGELOG.md da raiz vai embutido na dll da Web");
        Novidades.Todas[0].Versao.Should().NotBeNull();
        Novidades.Todas[0].Itens.Should().NotBeEmpty();
    }

    [Fact]
    public void Every_configuration_field_has_a_short_hint()
    {
        var campos = typeof(ConfiguracaoForm).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(ConfiguracaoForm.Observacao))
            .Select(p => p.Name);

        CamposConfiguracao.Todos.Select(c => c.Propriedade).Should().BeEquivalentTo(campos);
        CamposConfiguracao.Todos.Should().AllSatisfy(c =>
        {
            c.Dica.Should().NotBeNullOrWhiteSpace($"o campo {c.Propriedade} precisa de [Display(Description)]");
            c.Padrao.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public void Every_help_topic_has_its_partial_and_screens_point_to_existing_topics()
    {
        var views = Path.Combine(RaizDoRepositorio(), "src", "IntegracaoSolidesDP.Web", "Views");

        AjudaTopicos.Todos.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        AjudaTopicos.Todos.Should().AllSatisfy(t =>
            File.Exists(Path.Combine(views, "Ajuda", "Topicos", $"_{t.Id}.cshtml")).Should().BeTrue($"falta a partial do tópico {t.Id}"));
        AjudaTopicos.Todos.Where(t => t.Tela is not null).Should().AllSatisfy(t =>
            Directory.Exists(Path.Combine(views, t.Tela!)).Should().BeTrue($"o tópico {t.Id} aponta para a tela {t.Tela}, que não existe"));
    }

    [Fact]
    public void Technical_manual_pdf_is_embedded_in_the_web_assembly()
    {
        using var pdf = typeof(AjudaController).Assembly.GetManifestResourceStream(AjudaController.RecursoManualTecnico);

        pdf.Should().NotBeNull("o docs/manual-tecnico/manual-tecnico.pdf entra na dll da Web pelo .csproj");
        var cabecalho = new byte[5];
        pdf!.ReadExactly(cabecalho);
        System.Text.Encoding.ASCII.GetString(cabecalho).Should().Be("%PDF-");
    }

    private static string RaizDoRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IntegracaoSolidesDP.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
