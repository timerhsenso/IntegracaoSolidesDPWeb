using Microsoft.AspNetCore.Identity;

namespace IntegracaoSolidesDP.Web.Identity;

/// <summary>Usuário da Web. Nunca é excluído: é desativado (<see cref="Ativo"/> = false).</summary>
public sealed class ApplicationUser : IdentityUser
{
    public string NomeCompleto { get; set; } = string.Empty;

    public bool Ativo { get; set; } = true;

    /// <summary>Senha definida pelo administrador: o usuário troca no próximo acesso.</summary>
    public bool DeveTrocarSenha { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
}
