using Microsoft.AspNetCore.Identity;

namespace IntegracaoSolidesDP.Web.Identity;

/// <summary>Mensagens do Identity em português.</summary>
public sealed class IdentityErrorDescriberPtBr : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Erro(nameof(DefaultError), "Ocorreu um erro desconhecido.");

    public override IdentityError PasswordMismatch() => Erro(nameof(PasswordMismatch), "Senha atual incorreta.");

    public override IdentityError InvalidToken() => Erro(nameof(InvalidToken), "Token inválido.");

    public override IdentityError InvalidUserName(string? userName) =>
        Erro(nameof(InvalidUserName), $"O usuário '{userName}' é inválido: use letras, números, ponto, hífen ou sublinhado.");

    public override IdentityError DuplicateUserName(string userName) =>
        Erro(nameof(DuplicateUserName), $"O usuário '{userName}' já existe.");

    public override IdentityError PasswordTooShort(int length) =>
        Erro(nameof(PasswordTooShort), $"A senha deve ter pelo menos {length} caracteres.");

    public override IdentityError PasswordRequiresDigit() => Erro(nameof(PasswordRequiresDigit), "A senha deve ter pelo menos um número.");

    public override IdentityError PasswordRequiresLower() => Erro(nameof(PasswordRequiresLower), "A senha deve ter pelo menos uma letra minúscula.");

    public override IdentityError PasswordRequiresUpper() => Erro(nameof(PasswordRequiresUpper), "A senha deve ter pelo menos uma letra maiúscula.");

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Erro(nameof(PasswordRequiresNonAlphanumeric), "A senha deve ter pelo menos um caractere especial.");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Erro(nameof(PasswordRequiresUniqueChars), $"A senha deve ter pelo menos {uniqueChars} caracteres diferentes.");

    public override IdentityError UserAlreadyInRole(string role) => Erro(nameof(UserAlreadyInRole), $"O usuário já tem o perfil {role}.");

    public override IdentityError UserNotInRole(string role) => Erro(nameof(UserNotInRole), $"O usuário não tem o perfil {role}.");

    private static IdentityError Erro(string code, string description) => new() { Code = code, Description = description };
}
