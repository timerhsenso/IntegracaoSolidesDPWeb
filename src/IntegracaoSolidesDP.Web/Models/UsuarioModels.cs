using System.ComponentModel.DataAnnotations;

namespace IntegracaoSolidesDP.Web.Models;

public sealed record UsuarioListaItem(
    string Id,
    string Usuario,
    string Nome,
    string? Perfil,
    bool Ativo,
    bool DeveTrocarSenha,
    DateTimeOffset? BloqueadoAte,
    DateTimeOffset CriadoEm);

public sealed class UsuarioNovoViewModel
{
    [Required(ErrorMessage = "Informe o usuário.")]
    [StringLength(64, MinimumLength = 3, ErrorMessage = "O usuário deve ter entre 3 e 64 caracteres.")]
    [Display(Name = "Usuário (login)")]
    public string Usuario { get; set; } = string.Empty;

    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(120, ErrorMessage = "O nome deve ter até 120 caracteres.")]
    [Display(Name = "Nome completo")]
    public string NomeCompleto { get; set; } = string.Empty;

    [Required(ErrorMessage = "Escolha o perfil.")]
    [Display(Name = "Perfil")]
    public string Perfil { get; set; } = Identity.Perfis.Consulta;

    [Required(ErrorMessage = "Informe a senha inicial.")]
    [DataType(DataType.Password)]
    [Display(Name = "Senha inicial (o usuário troca no primeiro acesso)")]
    public string Senha { get; set; } = string.Empty;
}

public sealed class UsuarioEditarViewModel
{
    public string Id { get; set; } = string.Empty;

    [Display(Name = "Usuário (login)")]
    public string? Usuario { get; set; }

    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(120, ErrorMessage = "O nome deve ter até 120 caracteres.")]
    [Display(Name = "Nome completo")]
    public string NomeCompleto { get; set; } = string.Empty;

    [Required(ErrorMessage = "Escolha o perfil.")]
    [Display(Name = "Perfil")]
    public string Perfil { get; set; } = Identity.Perfis.Consulta;

    public bool Ativo { get; set; }

    public bool EhOProprioUsuario { get; set; }
}
