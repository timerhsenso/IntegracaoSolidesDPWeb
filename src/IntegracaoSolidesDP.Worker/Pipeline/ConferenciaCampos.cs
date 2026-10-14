using System.Globalization;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Conferência DP x RHSenso: para cada colaborador ativo e vinculado, compara o que a integração enviaria hoje com o que
/// está no Sólides DP. O find-all (uma leitura por empresa) diz quem existe e quem está desligado; os campos vêm do
/// employee/find de cada colaborador, porque o find-all da API real não traz CTPS, série, telefone, código do cargo nem
/// local. Não grava nada no DP: as divergências vão para solidesdp.conferencia e a Web oferece "Reenviar do RHSenso".
/// O que é do DP por regra não é comparado: vigência, escala, regra de ponto e Código Externo.
/// </summary>
public sealed class ConferenciaCampos(
    ISourceReader source,
    IStateStore state,
    IConferenciaStore conferencias,
    SyncSettingsLoader settingsLoader,
    SyncOptionsAccessor syncOptions,
    SolidesDpAccount account,
    ISolidesDpClient api,
    EmployeeMapper mapper,
    Notificador notificador,
    EpochDates dates,
    TimeProvider clock,
    ILogger<ConferenciaCampos> logger)
{
    /// <summary>
    /// Confere a empresa pedida ou todas as habilitadas. Com <paramref name="desde"/>, pula a empresa já conferida a partir
    /// dali (a agendada não repete a pedida na mesma semana). A agendada com divergência avisa por e-mail.
    /// </summary>
    /// <exception cref="SyncAbortedException">Configuração ilegível ou empresa pedida não habilitada.</exception>
    public async Task<IReadOnlyList<Conferencia>> ExecutarAsync(
        int? cdempresa, string gatilho, string? solicitadoPor, DateTimeOffset? desde, CancellationToken ct)
    {
        var settings = await settingsLoader.LoadAsync(ct);
        var instancia = syncOptions.Configured.InstanceName;
        var empresas = settings.Empresas.Where(e => cdempresa is null || e.Cdempresa == cdempresa).ToList();
        if (cdempresa is { } pedida && empresas.Count == 0)
        {
            throw new SyncAbortedException(FormattableString.Invariant($"A empresa {pedida} não está habilitada na integração."));
        }

        var feitas = new List<Conferencia>();
        foreach (var empresa in empresas)
        {
            ct.ThrowIfCancellationRequested();
            if (desde is { } inicioSemana && await conferencias.UltimaAsync(instancia, empresa.Cdempresa, ct) is { } ultima && ultima >= inicioSemana)
            {
                continue;
            }

            var conferencia = await ConferirAsync(instancia, empresa, gatilho, solicitadoPor, ct);
            conferencia = conferencia with { Id = await conferencias.SalvarAsync(conferencia, ct) };
            feitas.Add(conferencia);
            logger.LogInformation(
                "Conferência {Id} da empresa {Empresa}: {Conferidos} conferidos, {Pessoas} com divergência ({Divergencias} campos){Erro}",
                conferencia.Id, empresa.Cdempresa, conferencia.Conferidos, conferencia.Pessoas, conferencia.Divergencias,
                conferencia.Erro is null ? string.Empty : ". Não concluída: " + conferencia.Erro);
            if (gatilho == GatilhosConferencia.Agendada)
            {
                await notificador.AvisarConferenciaAsync(conferencia, CancellationToken.None);
            }
        }

        return feitas;
    }

    private async Task<Conferencia> ConferirAsync(string instancia, EmpresaSettings empresa, string gatilho, string? solicitadoPor, CancellationToken ct)
    {
        var inicio = clock.GetUtcNow();
        var resultado = new Conferencia
        {
            Instancia = instancia,
            Cdempresa = empresa.Cdempresa,
            IniciadaEm = inicio,
            ConcluidaEm = inicio,
            Gatilho = gatilho,
            SolicitadoPor = solicitadoPor,
        };

        if (empresa.Token is not { } token)
        {
            return resultado with { Erro = "a empresa não tem token do Sólides DP (tela Empresas)", ConcluidaEm = clock.GetUtcNow() };
        }

        syncOptions.Use(empresa.Options);
        account.Use(empresa.Cdempresa, token);
        try
        {
            var (conferidos, linhas) = await CompararEmpresaAsync(empresa, ct);
            return resultado with
            {
                ConcluidaEm = clock.GetUtcNow(),
                Conferidos = conferidos,
                Pessoas = linhas.Select(l => l.Cpf).Distinct(StringComparer.Ordinal).Count(),
                Divergencias = linhas.Count,
                Linhas = linhas,
            };
        }
        catch (SyncAbortedException ex)
        {
            return resultado with { Erro = ex.Message, ConcluidaEm = clock.GetUtcNow() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Conferência da empresa {Empresa} falhou", empresa.Cdempresa);
            return resultado with { Erro = ex.Message, ConcluidaEm = clock.GetUtcNow() };
        }
        finally
        {
            account.Clear();
        }
    }

    private async Task<(int Conferidos, List<DivergenciaCampo> Linhas)> CompararEmpresaAsync(EmpresaSettings empresa, CancellationToken ct)
    {
        var options = empresa.Options;
        var escopo = SyncPipeline.Escopo(empresa, await source.ReadWorkplacesAsync(ct));
        var rows = await source.ReadEmployeesAsync(options.TiposColaborador.ToList(), [empresa.Cdempresa], ct);
        var plan = EmployeeClassifier.Classify(rows, options, dates.Today(clock), escopo);
        var vinculos = await state.LoadEntityStatesAsync(empresa.Cdempresa, EntityTypes.Employee, ct);

        var ativos = await api.FindAllEmployeesAsync(ct);
        ReferenceResolver.ThrowIfUnauthorized(ativos);
        var todos = await api.FindAllEmployeesIncludingFiredAsync(ct);
        ReferenceResolver.ThrowIfUnauthorized(todos);
        if (!ativos.IsSuccess || ativos.Value is null || !todos.IsSuccess || todos.Value is null)
        {
            var falha = ativos.IsSuccess ? todos : ativos;
            throw new SyncAbortedException($"Falha ao listar os colaboradores do Sólides DP: {falha.Outcome} {falha.Message}");
        }

        var ativosIds = ativos.Value.Select(e => e.Id).ToHashSet();
        var porId = new Dictionary<long, EmployeeDto>();
        foreach (var employee in todos.Value.Concat(ativos.Value))
        {
            porId[employee.Id] = employee;
        }

        Dictionary<string, CompanyDto>? empresasPorCnpj = null;
        if (options.CompanyMode == CompanyMode.ResolveByCnpj)
        {
            var companies = await api.GetCompaniesAsync(ct);
            ReferenceResolver.ThrowIfUnauthorized(companies);
            empresasPorCnpj = (companies.Value ?? [])
                .Where(c => EmployeeMapper.Digits(c.Cnpj) is not null)
                .GroupBy(c => EmployeeMapper.Digits(c.Cnpj)!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        var empresasPorId = empresasPorCnpj?.Values.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var conferidos = 0;
        var naoLidos = 0;
        var linhas = new List<DivergenciaCampo>();
        foreach (var row in plan.Active.OrderBy(r => r.Nomatric, StringComparer.Ordinal))
        {
            if (row.Chave is not { } cpf || !vinculos.TryGetValue(cpf, out var vinculo)
                || vinculo.RemoteId is not { } remoteId || vinculo.Status == EntityStatuses.Dismissed)
            {
                continue;
            }

            DivergenciaCampo Linha(string campo, string? valorRh, string? valorDp) => new()
            {
                Rotulo = row.Rotulo,
                Nome = row.Nome?.Trim(),
                Cpf = cpf,
                CpfMascarado = VinculacaoReport.MascararCpf(cpf),
                CadastroDp = remoteId,
                Campo = campo,
                RhSenso = valorRh,
                Dp = valorDp,
            };

            if (!porId.TryGetValue(remoteId, out var resumo))
            {
                conferidos++;
                linhas.Add(Linha(CamposConferencia.Cadastro, $"vinculado ao cadastro {remoteId.ToString(CultureInfo.InvariantCulture)}", "não encontrado"));
                continue;
            }

            if (!ativosIds.Contains(remoteId))
            {
                conferidos++;
                var quando = resumo.ResignationDate is { } ms ? " em " + Data(dates.ToDate(ms)) : string.Empty;
                linhas.Add(Linha(CamposConferencia.Situacao, "ativo", "desligado" + quando));
                continue;
            }

            long? companyId = null;
            if (empresasPorCnpj is not null && EmployeeMapper.Digits(row.Cnpj) is { } cnpj && empresasPorCnpj.TryGetValue(cnpj, out var company))
            {
                companyId = company.Id;
            }

            if (mapper.Map(row, options.GoLiveDate, companyId).Payload is not { } payload)
            {
                // Dado inválido no RHSenso: já aparece como pendência nas execuções.
                continue;
            }

            // O find-all vem resumido (sem CTPS, série, telefone, código do cargo e local): compara com o cadastro completo.
            var completo = await api.FindEmployeeByIdAsync(remoteId, ct);
            ReferenceResolver.ThrowIfUnauthorized(completo);
            if (!completo.IsSuccess || completo.Value is not { } dp)
            {
                naoLidos++;
                logger.LogWarning(
                    "Conferência da empresa {Empresa}: cadastro {Cadastro} ({Rotulo}) não foi lido no Sólides DP e ficou de fora: {Outcome} {Mensagem}",
                    empresa.Cdempresa, remoteId, row.Rotulo, completo.Outcome, completo.Message);
                continue;
            }

            conferidos++;
            linhas.AddRange(Comparar(payload, dp, dates, empresasPorId).Select(d => Linha(d.Campo, d.RhSenso, d.Dp)));
        }

        if (naoLidos > 0 && conferidos == 0)
        {
            throw new SyncAbortedException(FormattableString.Invariant(
                $"nenhum cadastro foi lido no Sólides DP ({naoLidos} tentativas): veja o log do serviço"));
        }

        return (conferidos, linhas);
    }

    /// <summary>
    /// Os campos diferentes entre o que seria enviado (<paramref name="rh"/>) e o cadastro do DP. Campo que a integração
    /// não envia (vazio no RHSenso) não é comparado: o DP pode ter o seu valor.
    /// </summary>
    public static IReadOnlyList<(string Campo, string? RhSenso, string? Dp)> Comparar(
        EmployeeRequest rh, EmployeeDto dp, EpochDates dates, IReadOnlyDictionary<long, CompanyDto>? empresasPorId = null)
    {
        ArgumentNullException.ThrowIfNull(rh);
        ArgumentNullException.ThrowIfNull(dp);
        ArgumentNullException.ThrowIfNull(dates);
        var diferencas = new List<(string, string?, string?)>();

        void Texto(string campo, string? valorRh, string? valorDp, Func<string?, string> normalizar)
        {
            if (string.IsNullOrWhiteSpace(valorRh))
            {
                return;
            }

            if (!string.Equals(normalizar(valorRh), normalizar(valorDp), StringComparison.Ordinal))
            {
                diferencas.Add((campo, valorRh.Trim(), Vazio(valorDp)));
            }
        }

        void DataCampo(string campo, long? valorRh, long? valorDp)
        {
            if (valorRh is not { } msRh)
            {
                return;
            }

            var diaRh = dates.ToDate(msRh);
            // O DP devolve meia-noite local ou meia-noite UTC conforme o endpoint: vale o dia em qualquer das duas leituras.
            var confere = valorDp is { } msDp
                          && (dates.ToDate(msDp) == diaRh || DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(msDp).UtcDateTime) == diaRh);
            if (!confere)
            {
                diferencas.Add((campo, Data(diaRh), valorDp is { } d ? Data(dates.ToDate(d)) : null));
            }
        }

        Texto(CamposConferencia.Nome, rh.Name, dp.Name, Palavras);
        Texto(CamposConferencia.Pis, rh.Pis, dp.Pis, Digitos);
        Texto(CamposConferencia.Ctps, rh.Ctps, dp.Ctps, Alfanumerico);
        Texto(CamposConferencia.Serie, rh.Series, dp.Series, Alfanumerico);
        DataCampo(CamposConferencia.Nascimento, rh.BirthDate, dp.BirthDate);
        DataCampo(CamposConferencia.Admissao, rh.AdmissionDate, dp.AdmissionDate);
        Texto(CamposConferencia.Email, rh.Email, dp.Email, Palavras);
        Texto(CamposConferencia.Telefone, rh.Phone, dp.Phone, Digitos);
        Texto(CamposConferencia.Sexo, rh.Gender, dp.Gender, Palavras);

        if (!string.IsNullOrWhiteSpace(rh.JobRoleExternalId)
            && !string.Equals(rh.JobRoleExternalId.Trim(), dp.JobRole?.ExternalId?.Trim(), StringComparison.Ordinal))
        {
            diferencas.Add((CamposConferencia.Cargo, rh.JobRoleExternalId.Trim(), Rotular(dp.JobRole?.ExternalId, dp.JobRole?.Description)));
        }

        if (!string.IsNullOrWhiteSpace(rh.WorkplaceExternalId))
        {
            var localRh = rh.WorkplaceExternalId.Trim();
            var locaisDp = LocaisAtuais(dp);
            if (!locaisDp.Any(l => string.Equals(localRh, l.ExternalId?.Trim(), StringComparison.Ordinal)))
            {
                var rotulos = locaisDp.Select(l => Rotular(l.ExternalId, l.Name)).OfType<string>().ToList();
                diferencas.Add((CamposConferencia.Local, localRh, rotulos.Count == 0 ? null : string.Join(", ", rotulos)));
            }
        }

        if (rh.Company is { } companyRh && dp.Company?.Id != companyRh)
        {
            diferencas.Add((CamposConferencia.Empresa, NomeEmpresa(companyRh, empresasPorId), dp.Company is { } c ? NomeEmpresa(c.Id, empresasPorId, c) : null));
        }

        return diferencas;
    }

    /// <summary>
    /// Locais atuais no DP: o <c>currentWorkplaceDTO</c>, quando vier; senão os ativos da <c>workplaceList</c> (é onde a
    /// API real devolve o local).
    /// </summary>
    private static IReadOnlyList<WorkplaceDto> LocaisAtuais(EmployeeDto dp) =>
        dp.CurrentWorkplace is { } atual
            ? [atual]
            : (dp.WorkplaceList ?? []).Where(l => l.Active != false).ToList();

    private static string Data(DateOnly dia) => dia.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string? Vazio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>Espaços repetidos não contam; maiúsculas e minúsculas também não.</summary>
    private static string Palavras(string? valor) =>
        string.Join(' ', (valor ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();

    private static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static string Alfanumerico(string? valor) =>
        new((valor ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Rotular(string? codigo, string? nome) =>
        (codigo?.Trim(), nome?.Trim()) switch
        {
            ({ Length: > 0 } c, { Length: > 0 } n) => $"{c} - {n}",
            ({ Length: > 0 } c, _) => c,
            (_, { Length: > 0 } n) => n,
            _ => null,
        };

    private static string NomeEmpresa(long id, IReadOnlyDictionary<long, CompanyDto>? empresasPorId, CompanyDto? doDp = null)
    {
        var empresa = empresasPorId?.GetValueOrDefault(id) ?? doDp;
        var nome = empresa?.FantasyName ?? empresa?.SocialReason;
        var codigo = id.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(nome) ? codigo : $"{codigo} - {nome.Trim()}";
    }
}
