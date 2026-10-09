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
