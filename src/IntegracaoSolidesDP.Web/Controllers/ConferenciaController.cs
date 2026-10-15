using System.Globalization;
using System.Text;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Conferência DP x RHSenso: o que está diferente no Sólides DP para os colaboradores vinculados. Quem confere é o
/// serviço (semanal ou pelo pedido "Conferir campos"); aqui se vê o resultado e se pede "Reenviar do RHSenso", que só
/// marca o vínculo para a próxima execução reenviar o cadastro. Quem está só no Sólides DP (e não vai para a folha)
/// pode ser "Ignorado": sai das próximas conferências e do relatório de vinculação da empresa.
/// </summary>
[ExigeGestaoPreparada]
public sealed class ConferenciaController(IConferenciaStore conferencias, IIgnoradoStore ignorados, Auditoria auditoria) : Controller
{
    private const int NaLista = 60;

    public async Task<IActionResult> Index() => View(await conferencias.RecentesAsync(NaLista, HttpContext.RequestAborted));

    public async Task<IActionResult> Detalhe(long id)
    {
        var conferencia = await conferencias.ObterAsync(id, HttpContext.RequestAborted);
        return conferencia is null ? NotFound() : View(conferencia with { Linhas = [] });
    }

    /// <summary>
    /// Linhas para a grade (sem o CPF completo). <c>chave</c> identifica a pessoa na grade: a matrícula, ou "dp-{cadastro}"
    /// para quem está só no Sólides DP; <c>ignorado</c> = já foi ignorado depois desta conferência.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Linhas(long id)
    {
        var ct = HttpContext.RequestAborted;
        var conferencia = await conferencias.ObterAsync(id, ct);
        if (conferencia is null)
        {
            return NotFound();
        }

        var vigentes = conferencia.Linhas.Any(SoNoDp)
            ? await ignorados.VigentesAsync(conferencia.Cdempresa, ct)
            : Array.Empty<ColaboradorIgnorado>();
        return Json(conferencia.Linhas.Select(l => new
        {
            chave = SoNoDp(l) ? Chave(l) : l.Rotulo,
            soNoDp = SoNoDp(l),
            ignorado = SoNoDp(l) && l.CadastroDp is { } cadastro && vigentes.Any(i => i.Corresponde(cadastro, CpfDaLinha(l))),
            rotulo = l.Rotulo,
            nome = l.Nome,
            cpf = l.CpfMascarado,
            cadastroDp = l.CadastroDp,
            campo = l.Campo,
            rhSenso = l.RhSenso,
            dp = l.Dp,
        }));
    }

    [HttpGet]
    public async Task<IActionResult> Csv(long id)
    {
        var ct = HttpContext.RequestAborted;
        var conferencia = await conferencias.ObterAsync(id, ct);
        if (conferencia is null)
        {
            return NotFound();
        }

        var arquivo = string.Create(CultureInfo.InvariantCulture,
            $"conferencia-empresa{conferencia.Cdempresa}-{conferencia.IniciadaEm.UtcDateTime:yyyyMMdd-HHmm}.csv");
        var csv = new StringBuilder("Matrícula;Nome;CPF;Cadastro no DP;Campo;RHSenso;Sólides DP\r\n");
        foreach (var l in conferencia.Linhas)
        {
            csv.Append(Campo(l.Rotulo)).Append(';').Append(Campo(l.Nome)).Append(';').Append(Campo(l.CpfMascarado)).Append(';')
                .Append(Campo(l.CadastroDp?.ToString(CultureInfo.InvariantCulture))).Append(';').Append(Campo(l.Campo)).Append(';')
                .Append(Campo(l.RhSenso)).Append(';').Append(Campo(l.Dp)).Append("\r\n");
        }

        await auditoria.RegistrarAsync(AcoesAuditoria.RelatorioBaixado, $"{arquivo} (conferência {id.ToString(CultureInfo.InvariantCulture)})", ct: ct);
        var conteudo = Encoding.UTF8.GetBytes(csv.ToString());
        var bytes = new byte[Encoding.UTF8.Preamble.Length + conteudo.Length];
        Encoding.UTF8.Preamble.CopyTo(bytes);
        conteudo.CopyTo(bytes, Encoding.UTF8.Preamble.Length);
        return File(bytes, "text/csv; charset=utf-8", arquivo);
    }

    /// <summary>
    /// "Reenviar do RHSenso" para as pessoas marcadas (pela matrícula da grade): a próxima execução reenvia o cadastro
    /// inteiro e o RHSenso prevalece. Numa empresa em simulação, a simulação mostra o que seria enviado.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Operar)]
    public async Task<IActionResult> Reenviar(long id, string[]? rotulos)
    {
        var ct = HttpContext.RequestAborted;
        var conferencia = await conferencias.ObterAsync(id, ct);
        if (conferencia is null)
        {
            return NotFound();
        }

        var marcados = (rotulos ?? []).ToHashSet(StringComparer.Ordinal);
        var pessoas = conferencia.Linhas
            .Where(l => !SoNoDp(l) && marcados.Contains(l.Rotulo))
            .GroupBy(l => l.Cpf, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (pessoas.Count == 0)
        {
            TempData["Erro"] = "Marque pelo menos um colaborador da lista.";
            return RedirectToAction(nameof(Detalhe), new { id });
        }

        var marcadas = await conferencias.MarcarReenvioAsync(conferencia.Cdempresa, pessoas.Select(p => p.Cpf).ToList(), ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.ReenvioSolicitado, string.Create(CultureInfo.InvariantCulture,
            $"empresa {conferencia.Cdempresa}, conferência {id}: {string.Join(", ", pessoas.Select(p => p.Rotulo))}"), ct: ct);
        TempData["Sucesso"] = marcadas == pessoas.Count
            ? $"{marcadas} colaborador(es) marcado(s): a próxima execução da empresa {conferencia.Cdempresa} reenvia o cadastro do RHSenso."
            : $"{marcadas} de {pessoas.Count} colaborador(es) marcado(s); os demais já estavam aguardando reenvio ou não estão mais vinculados.";
        return RedirectToAction(nameof(Detalhe), new { id });
    }

    /// <summary>
    /// "Ignorar" quem está só no Sólides DP (marcados pelo id do cadastro no DP): a pessoa não aparece mais nas conferências
    /// nem no relatório de vinculação desta empresa. Nada muda no Sólides DP. Se o CPF aparecer ativo na folha da empresa,
    /// o serviço tira da lista sozinho.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Operar)]
    public async Task<IActionResult> Ignorar(long id, long[]? cadastros)
    {
        var ct = HttpContext.RequestAborted;
        var conferencia = await conferencias.ObterAsync(id, ct);
        if (conferencia is null)
        {
            return NotFound();
        }

        var marcados = (cadastros ?? []).ToHashSet();
        var pessoas = conferencia.Linhas
            .Where(l => SoNoDp(l) && l.CadastroDp is { } cadastro && marcados.Contains(cadastro))
            .GroupBy(l => l.CadastroDp!.Value)
            .Select(g => g.First())
            .ToList();
        if (pessoas.Count == 0)
        {
            TempData["Erro"] = "Marque pelo menos uma pessoa \"Só no Sólides DP\" da lista.";
            return RedirectToAction(nameof(Detalhe), new { id });
        }

        var incluidos = await ignorados.IgnorarAsync(conferencia.Cdempresa,
            pessoas.Select(p => new NovoIgnorado(CpfDaLinha(p), p.CadastroDp!.Value, p.Nome)).ToList(), HttpContext.Usuario(), ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.IgnoradoIncluido, string.Create(CultureInfo.InvariantCulture,
            $"empresa {conferencia.Cdempresa}, conferência {id}: {string.Join(", ", pessoas.Select(p => FormattableString.Invariant($"{p.Nome} (cadastro {p.CadastroDp} no DP)")))}"), ct: ct);
        TempData["Sucesso"] = incluidos == pessoas.Count
            ? $"{incluidos} pessoa(s) ignorada(s) na empresa {conferencia.Cdempresa}: não aparecem mais na conferência nem no relatório de vinculação. A lista fica na tela Empresas."
            : $"{incluidos} de {pessoas.Count} pessoa(s) ignorada(s); as demais já estavam ignoradas.";
        return RedirectToAction(nameof(Detalhe), new { id });
    }

    private static bool SoNoDp(DivergenciaCampo linha) => linha.Campo == CamposConferencia.SoNoDp;

    private static string Chave(DivergenciaCampo linha) => string.Create(CultureInfo.InvariantCulture, $"dp-{linha.CadastroDp}");

    /// <summary>O CPF da linha "Só no Sólides DP"; null quando o cadastro do DP não tem CPF (a chave é "dp-{cadastro}").</summary>
    private static string? CpfDaLinha(DivergenciaCampo linha) => linha.Cpf.StartsWith("dp-", StringComparison.Ordinal) ? null : linha.Cpf;

    private static string Campo(string? valor)
    {
        var texto = CsvSeguro.Neutralizar(valor ?? string.Empty);
        return texto.IndexOfAny([';', '"', '\r', '\n']) < 0 ? texto : "\"" + texto.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
