namespace SolidesDP.Fake.Configuration;

/// <summary>Como os erros de validacao/negocio sao devolvidos ao cliente.</summary>
public enum ErrorStyle
{
    /// <summary>
    /// Endpoints cujo schema 200 e <c>ResponseEntity</c> / <c>Adjustment*ResponseDTO</c> respondem HTTP 200 com o erro no
    /// corpo; os demais respondem HTTP 4xx com corpo no estilo Spring.
    /// </summary>
    ResponseEntity,

    /// <summary>Todo erro de validacao/negocio vira HTTP 4xx (400/404/409) com corpo no estilo Spring.</summary>
    Http,
}

/// <summary>O que fazer, numa atualizacao com <c>allowUpdate=true</c>, com propriedades ausentes/nulas no request.</summary>
public enum UpdateOmittedFieldsMode
{
    /// <summary>Propriedades ausentes/nulas mantem o valor armazenado.</summary>
    Keep,

    /// <summary>Propriedades ausentes/nulas limpam o valor armazenado.</summary>
    Clear,
}

/// <summary>O que o <c>POST /employee/register</c> faz quando o externalId pertence a um colaborador demitido.</summary>
public enum RegisterFiredExternalIdMode
{
    /// <summary>Erro <c>employee_fired</c>.</summary>
    Error,

    /// <summary>Cria um novo colaborador (novo id) reaproveitando o externalId.</summary>
    CreateNew,

    /// <summary>Reativa o colaborador demitido (mesmo id) aplicando os dados do request.</summary>
    Reactivate,
}

/// <summary>O que o <c>POST /job-role/register</c> faz com um externalId que ja existe.</summary>
public enum JobRoleDuplicateExternalIdMode
{
    /// <summary>Erro <c>duplicate_external_id</c>.</summary>
    Error,

    /// <summary>Cria outro cargo com o mesmo externalId.</summary>
    Duplicate,

    /// <summary>Atualiza o cargo existente.</summary>
    Update,
}

/// <summary>
/// Perfil de comportamento do fake. Todos os valores sao trocaveis em runtime por <c>PUT /_fake/behavior</c>;
/// <c>POST /_fake/reset</c> volta ao que foi configurado em <c>Fake:Behavior</c>.
/// </summary>
public sealed record FakeBehavior
{
    /// <summary>Estilo de resposta de erro.</summary>
    public ErrorStyle ErrorStyle { get; set; } = ErrorStyle.ResponseEntity;

    /// <summary>Se verdadeiro, exige <c>Authorization: Basic &lt;token&gt;</c>; senao aceita tambem o token puro.</summary>
    public bool RequireBasicPrefix { get; set; } = true;

    /// <summary>Rejeita requests com propriedades que nao existem na definition correspondente do Swagger.</summary>
    public bool RejectUnknownFields { get; set; } = true;

    /// <summary>CPF de outro colaborador nao demitido gera <c>duplicate_cpf</c> (salvo <c>doubleBindEmployee=true</c>).</summary>
    public bool UniqueCpfAmongActive { get; set; } = true;

    /// <summary>Semantica de propriedades omitidas em atualizacoes.</summary>
    public UpdateOmittedFieldsMode UpdateOmittedFields { get; set; } = UpdateOmittedFieldsMode.Keep;

    /// <summary>Comportamento do register para externalId de colaborador demitido.</summary>
    public RegisterFiredExternalIdMode RegisterFiredExternalId { get; set; } = RegisterFiredExternalIdMode.Error;

    /// <summary>Comportamento do register de cargo para externalId repetido.</summary>
    public JobRoleDuplicateExternalIdMode JobRoleDuplicateExternalId { get; set; } = JobRoleDuplicateExternalIdMode.Error;

    /// <summary>Lancamento sobreposto a outro (nao excluido) do mesmo colaborador gera <c>overlap</c>.</summary>
    public bool RejectOverlappingAdjustments { get; set; } = true;
}
