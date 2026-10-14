using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Os ignorados de uma empresa já conferidos com a folha.</summary>
/// <param name="Vigentes">Continuam ignorados.</param>
/// <param name="Voltaram">Apareceram ativos na folha da empresa e saíram da lista nesta leitura.</param>
public sealed record IgnoradosDaEmpresa(IReadOnlyList<ColaboradorIgnorado> Vigentes, IReadOnlyList<IgnoradoQueVoltou> Voltaram)
{
    public static readonly IgnoradosDaEmpresa Nenhum = new([], []);

    /// <summary>O cadastro do DP está ignorado (pelo CPF, ou pelo id do cadastro quando não há CPF).</summary>
    public bool Contem(long cadastroDp, string? cpf) => Vigentes.Any(i => i.Corresponde(cadastroDp, cpf));
}

/// <summary>Ignorado que apareceu ativo na folha: saiu da lista e segue as regras normais da integração.</summary>
/// <param name="Ignorado">Como estava na lista.</param>
/// <param name="Rotulo">"empresa-matrícula" em que ele apareceu.</param>
public sealed record IgnoradoQueVoltou(ColaboradorIgnorado Ignorado, string Rotulo);

/// <summary>
/// Ignorados da conferência ("Só no Sólides DP" que o RH mandou ignorar). Ao ler a lista de uma empresa, quem estiver
/// ATIVO na folha dela (qualquer tipo de colaborador ou filial; situação que não é demissão, transferência nem ignorada)
/// sai da lista sozinho e volta para as regras normais: se for de um tipo que a integração não leva (ex.: estagiário),
/// simplesmente não é enviado, e volta a aparecer como "Só no Sólides DP" com esse motivo.
/// </summary>
public sealed class ColaboradoresIgnorados(IIgnoradoStore store, ISourceReader source, ILogger<ColaboradoresIgnorados> logger)
{
    public async Task<IgnoradosDaEmpresa> CarregarAsync(int cdempresa, SyncOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var vigentes = await store.VigentesAsync(cdempresa, ct);
        if (vigentes.Count == 0)
        {
            return IgnoradosDaEmpresa.Nenhum;
        }

        var naFolha = await AtivosNaFolhaAsync(cdempresa, vigentes, options, ct);
        var ficam = new List<ColaboradorIgnorado>();
        var voltaram = new List<IgnoradoQueVoltou>();
        foreach (var ignorado in vigentes)
        {
            if (ignorado.Cpf is not { } cpf || !naFolha.TryGetValue(cpf, out var linha))
            {
                ficam.Add(ignorado);
                continue;
            }

            var motivo = $"apareceu ativo na folha (matrícula {linha.Rotulo}): voltou a seguir as regras da integração";
            if (await store.RemoverAsync(cdempresa, ignorado.Id, SqlIgnoradoStore.Servico, motivo, ct))
            {
                logger.LogInformation(
                    "Empresa {Empresa}: cadastro {Cadastro} ({Nome}) saiu da lista de ignorados: {Motivo}",
                    cdempresa, ignorado.CadastroDp, ignorado.NomeDp, motivo);
                voltaram.Add(new IgnoradoQueVoltou(ignorado, linha.Rotulo));
            }
        }

        return new IgnoradosDaEmpresa(ficam, voltaram);
    }

    private async Task<Dictionary<string, EmployeeRow>> AtivosNaFolhaAsync(
        int cdempresa, IReadOnlyList<ColaboradorIgnorado> vigentes, SyncOptions options, CancellationToken ct)
    {
        var ativos = new Dictionary<string, EmployeeRow>(StringComparer.Ordinal);
        var cpfs = vigentes.Select(i => i.Cpf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (cpfs.Count == 0)
        {
            return ativos;
        }

        var ignoradas = options.SituacoesIgnoradas.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var desligamento = options.SituacoesDesligamento.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var transferido = options.SituacaoTransferido.Trim();
        foreach (var linha in await source.ReadEmployeesByCpfAsync(cpfs, ct))
        {
            if (linha.Chave is not { } cpf || linha.Cdempresa != cdempresa || linha.SituacaoDeDesligamento)
            {
                continue;
            }

            if (linha.Situacao is { } situacao && (ignoradas.Contains(situacao) || desligamento.Contains(situacao) || situacao == transferido))
            {
                continue;
            }

            ativos.TryAdd(cpf, linha);
        }

        return ativos;
    }
}
