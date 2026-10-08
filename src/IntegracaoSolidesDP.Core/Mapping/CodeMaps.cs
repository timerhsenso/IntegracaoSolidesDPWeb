using System.Collections.Frozen;

namespace IntegracaoSolidesDP.Worker.Mapping;

/// <summary>
/// Tabelas de código do RHSenso → enums do Sólides DP. Os códigos de origem foram
/// conferidos no bd_rhu_adn: taux2 (tabelas 06 e 60), tgin1, tcre1 e a codificação
/// RAIS de func1.cod_raca (validada contra os eventos S-2200 já enviados ao eSocial).
/// </summary>
public static class CodeMaps
{
    /// <summary>func1.cdsexo.</summary>
    public static readonly FrozenDictionary<string, string> Gender = new Dictionary<string, string>
    {
        ["F"] = "FEMININO",
        ["M"] = "MASCULINO",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>func1.cdestcivil (taux2 tabela 06). "O" (outros) não tem equivalente e é omitido.</summary>
    public static readonly FrozenDictionary<string, string> MaritalStatus = new Dictionary<string, string>
    {
        ["C"] = "CASADO",
        ["D"] = "SEPARADO", // DESQUITADO
        ["I"] = "DIVORCIADO",
        ["S"] = "SOLTEIRO",
        ["V"] = "VIUVO",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>func1.cdinstruc (tgin1). 10 = MBA e 11 = pós-graduado no RHSenso.</summary>
    public static readonly FrozenDictionary<string, string> EducationLevel = new Dictionary<string, string>
    {
        ["01"] = "ANALFABETO",
        ["02"] = "ATE_5_ANO_INCOMPLETO",
        ["03"] = "ATE_5_ANO_COMPLETO",
        ["04"] = "ATE_9_ANO_INCOMPLETO",
        ["05"] = "FUNDAMENTAL_COMPLETO",
        ["06"] = "MEDIO_INCOMPLETO",
        ["07"] = "MEDIO_COMPLETO",
        ["08"] = "SUPERIOR_INCOMPLETO",
        ["09"] = "SUPERIOR_COMPLETO",
        ["10"] = "MBA",
        ["11"] = "POS_GRADUACAO_COMPLETO",
    }.ToFrozenDictionary();

    /// <summary>func1.cod_raca, codificação RAIS.</summary>
    public static readonly FrozenDictionary<int, string> RaceColor = new Dictionary<int, string>
    {
        [1] = "INDIGENA",
        [2] = "BRANCA",
        [4] = "PRETA",
        [6] = "AMARELA",
        [8] = "PARDA",
        [9] = "NAO_INFORMADO",
    }.ToFrozenDictionary();

    /// <summary>func1.tpcolab (taux2 tabela 60) sincronizados.</summary>
    public static readonly FrozenDictionary<int, string> LaborRelationship = new Dictionary<int, string>
    {
        [1] = "CLT",     // EMPREGADO
        [2] = "ESTAGIO", // ESTAGIÁRIO
    }.ToFrozenDictionary();

    public const int TipoColaboradorEstagiario = 2;

    /// <summary>func1.cdcausres (tcre1) → DismissDTO.resignationReason. Sobrescrevível em Sync:MotivoDemissaoMap.</summary>
    public static readonly FrozenDictionary<string, string> DefaultResignationReason = new Dictionary<string, string>
    {
        ["10"] = "COM_JUSTA_CAUSA",
        ["11"] = "SEM_JUSTA_CAUSA",
        ["12"] = "TERMINO_CONTRATO_TERMO",
        ["13"] = "ANTECIPADA_TERMO_EMPREGADO",
        ["14"] = "ANTECIPADA_TERMO_EMPREGADOR",
        ["18"] = "ACORDO_ENTRE_PARTES",
        ["20"] = "RESCISAO_INDIRETA",
        ["21"] = "PEDIDO_DEMISSAO_COLABORADOR",
        ["30"] = "TRANSFERENCIA_GRUPO_EMPRESARIAL",
        ["50"] = "OUTROS",
        ["60"] = "FALECIMENTO_EMPREGADO",
        ["70"] = "APOSENTADORIA_EXCETO_INVALIDEZ",
    }.ToFrozenDictionary();

    public const string ResignationReasonFallback = "OUTROS";
    public const string ResignationReasonTransfer = "TRANSFERENCIA_GRUPO_EMPRESARIAL";

    /// <summary>Valores aceitos por DismissDTO.resignationReason (Swagger do DP).</summary>
    public static readonly FrozenSet<string> ResignationReasons = new[]
    {
        "PEDIDO_DEMISSAO_COLABORADOR", "DEMISSAO_EMPRESA", "ESTAGIARIO_EFETIVADO", "TESTE_SISTEMA", "EXONERACAO", "OUTROS", "COM_JUSTA_CAUSA", "SEM_JUSTA_CAUSA", "ANTECIPADA_TERMO_EMPREGADOR", "ANTECIPADA_TERMO_EMPREGADO", "CULPA_RECIPROCA", "TERMINO_CONTRATO_TERMO", "PEDIDO_DEMISSAO_FUNCIONARIO", "RESCISAO_ART_394_483_CLT", "FALECIMENTO_EMPREGADOR_OPCAO", "FALECIMENTO_EMPREGADO", "TRANSFERENCIA_GRUPO_EMPRESARIAL", "TRANSFERENCIA_CONSORCIADO", "TRANSFERENCIA_SUCESSAO_EMPRESARIAL", "ENCERRAMENTO_EMPRESA_GERAL", "CONTRATO_NULO_ART_37_CF", "RESCISAO_INDIRETA", "APOSENTADORIA_COMPULSORIA", "APOSENTADORIA_IDADE", "APOSENTADORIA_IDADE_TEMPO", "REFORMA_MILITAR", "RESERVA_MILITAR", "DEMISSAO", "VACANCIA_CARGO", "PARALISACAO_ATOS_AUTORIDADE", "FORCA_MAIOR", "TERMINO_CESSAO_REQUISICAO", "REDISTRIBUICAO", "REFORMA_ADMINISTRATIVA", "MUDANCA_REGIME", "REVERSAO_REINTEGRACAO", "EXTRAVIO_MILITAR", "ACORDO_ENTRE_PARTES", "TRANSFERENCIA_DOMESTICO", "EXTINCAO_TRABALHO_INTERMITENTE", "MUDANCA_CPF", "REMOCAO_ORGAO_DECLARANTE", "APOSENTADORIA_EXCETO_INVALIDEZ", "APOSENTADORIA_INVALIDEZ", "TERMINO_MANDATO", "APRENDIZ_INADAPTACAO", "APRENDIZ_AUSENCIA_ESCOLAR", "TRANSFERENCIA_EMPRESA_INAPTA", "AGRUPAMENTO_CONTRATUAL", "EXCLUSAO_MILITAR_COM_EFEITOS", "EXCLUSAO_MILITAR_SEM_EFEITOS", "ENCERRAMENTO_EMPRESA", "FALECIMENTO_EMPREGADOR", "FALECIMENTO_DOMESTICO",
    }.ToFrozenSet();

    public static string ResignationReasonFor(string? causa, IDictionary<string, string> overrides)
    {
        var code = causa?.Trim() ?? string.Empty;
        if (overrides.TryGetValue(code, out var configured))
        {
            return configured;
        }

        return DefaultResignationReason.TryGetValue(code, out var known) ? known : ResignationReasonFallback;
    }
}
