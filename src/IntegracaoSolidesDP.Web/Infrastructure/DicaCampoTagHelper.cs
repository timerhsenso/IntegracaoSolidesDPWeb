using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>
/// <c>&lt;dica-campo for="Form.Campo" /&gt;</c>: a dica curta do campo (<c>[Display(Description)]</c>) e um "?"
/// que abre o trecho do manual sobre ele em outra aba, sem perder o que já foi digitado. <c>ajuda="id"</c> troca o trecho.
/// </summary>
[HtmlTargetElement("dica-campo", Attributes = "for", TagStructure = TagStructure.WithoutEndTag)]
public sealed class DicaCampoTagHelper(IUrlHelperFactory urlHelperFactory) : TagHelper
{
    [HtmlAttributeName("for")]
    public ModelExpression For { get; set; } = null!;

    /// <summary>Âncora do manual para o "?" (padrão: o campo da tela Configuração, <c>cfg-{Campo}</c>).</summary>
    [HtmlAttributeName("ajuda")]
    public string? Ajuda { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var propriedade = For.Metadata.PropertyName ?? For.Name;
        var url = urlHelperFactory.GetUrlHelper(ViewContext).Action("Index", "Ajuda") + "#" + (Ajuda ?? CamposConfiguracao.Ancora(propriedade));

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "form-text");
        output.Content.Append(For.Metadata.Description ?? string.Empty);
        output.Content.AppendHtml(" ");

        var link = new TagBuilder("a");
        link.Attributes["href"] = url;
        link.Attributes["target"] = "_blank";
        link.Attributes["rel"] = "noopener";
        link.Attributes["class"] = "dica-ajuda";
        link.Attributes["title"] = "Mais detalhes no manual (abre em outra aba)";
        link.Attributes["aria-label"] = "Mais detalhes no manual";
        link.InnerHtml.AppendHtml("<i class=\"bi bi-question-circle\"></i>");
        output.Content.AppendHtml(link);
    }
}
