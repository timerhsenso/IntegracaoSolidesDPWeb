namespace IntegracaoSolidesDP.Web.Identity;

/// <summary>Perfis (roles do Identity).</summary>
public static class Perfis
{
    /// <summary>Vê painel, execuções, o que foi migrado, pendências, comandos e configuração.</summary>
    public const string Consulta = "Consulta";

    /// <summary>Consulta + pede execuções, dry-run e verificações ao serviço.</summary>
    public const string Operador = "Operador";

    /// <summary>Operador + ativa/desativa, altera a configuração, gerencia usuários e vê a auditoria.</summary>
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> Todos = [Consulta, Operador, Admin];
}

/// <summary>Políticas de autorização.</summary>
public static class Politicas
{
    public const string Operar = "Operar";
    public const string Administrar = "Administrar";
}

/// <summary>Claims próprias gravadas no cookie de login.</summary>
public static class ClaimsWeb
{
    public const string Nome = "nome";
    public const string TrocaSenha = "troca_senha";
}
