using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Cifra e decifra o token do Sólides DP de cada empresa (solidesdp.empresa_token).</summary>
public interface ITokenProtector
{
    byte[] Protect(string token);

    /// <exception cref="CryptographicException">Dado corrompido ou cifrado com outra chave/máquina.</exception>
    string Unprotect(byte[] protegido);
}

/// <summary>
/// Proteção do token. A chave nunca fica no banco junto do token:
/// <list type="bullet">
/// <item><b>Chave configurada</b> (<see cref="ChaveBase64"/>, 32 bytes em base64): AES-256-GCM. Vale em qualquer
/// sistema operacional e permite Web e serviço em máquinas diferentes (a mesma chave nas duas).</item>
/// <item><b>Sem chave</b>, no Windows: DPAPI da máquina. Web e serviço precisam rodar na mesma máquina.</item>
/// </list>
/// </summary>
public sealed class TokenProtectionOptions
{
    public string? ChaveBase64 { get; set; }
}

/// <summary>
/// Formato gravado: 1 byte de versão + dados. Versão 1 = DPAPI da máquina; versão 2 = AES-256-GCM
/// (nonce 12 bytes | tag 16 bytes | texto cifrado). A versão é lida na hora de decifrar, então trocar
/// de modo não invalida os tokens já gravados enquanto a chave antiga continuar disponível.
/// </summary>
public sealed partial class TokenProtector(TokenProtectionOptions options) : ITokenProtector
{
    private const byte VersaoDpapi = 1;
    private const byte VersaoAesGcm = 2;
    private const int TamanhoNonce = 12;
    private const int TamanhoTag = 16;

    // Amarra o dado cifrado a este uso: outro programa na mesma máquina não decifra o blob por engano.
    private static readonly byte[] Proposito = Encoding.UTF8.GetBytes("IntegracaoSolidesDP.TokenSolidesDP.v1");

    public byte[] Protect(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var texto = Encoding.UTF8.GetBytes(token.Trim());

        if (Chave() is { } chave)
        {
            return AesGcmProtect(chave, texto);
        }

        if (OperatingSystem.IsWindows())
        {
            return [VersaoDpapi, .. Dpapi.Protect(texto, Proposito)];
        }

        throw new CryptographicException(
            "Sem chave para cifrar o token: fora do Windows, configure a chave dos tokens (Gestao:ChaveTokens no serviço e Web:ChaveTokens na Web).");
    }

    public string Unprotect(byte[] protegido)
    {
        ArgumentNullException.ThrowIfNull(protegido);
        if (protegido.Length < 2)
        {
            throw new CryptographicException("Token cifrado inválido.");
        }

        var dados = protegido.AsSpan(1);
        switch (protegido[0])
        {
            case VersaoAesGcm:
                var chave = Chave() ?? throw new CryptographicException(
                    "O token foi cifrado com a chave dos tokens, mas ela não está configurada nesta máquina.");
                return Encoding.UTF8.GetString(AesGcmUnprotect(chave, dados));
            case VersaoDpapi when OperatingSystem.IsWindows():
                return Encoding.UTF8.GetString(Dpapi.Unprotect(dados.ToArray(), Proposito));
            case VersaoDpapi:
                throw new CryptographicException("O token foi cifrado com a proteção do Windows (DPAPI) e só pode ser lido no Windows.");
            default:
                throw new CryptographicException($"Versão de token cifrado desconhecida: {protegido[0]}.");
        }
    }

    /// <summary>Gera uma chave nova para <see cref="TokenProtectionOptions.ChaveBase64"/>.</summary>
    public static string NovaChave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private byte[]? Chave()
    {
        if (string.IsNullOrWhiteSpace(options.ChaveBase64))
        {
            return null;
        }

        byte[] chave;
        try
        {
            chave = Convert.FromBase64String(options.ChaveBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("A chave dos tokens não é um base64 válido.", ex);
        }

        return chave.Length == 32
            ? chave
            : throw new CryptographicException($"A chave dos tokens precisa ter 32 bytes (tem {chave.Length}).");
    }

    private static byte[] AesGcmProtect(byte[] chave, byte[] texto)
    {
        var resultado = new byte[1 + TamanhoNonce + TamanhoTag + texto.Length];
        resultado[0] = VersaoAesGcm;
        var nonce = resultado.AsSpan(1, TamanhoNonce);
        var tag = resultado.AsSpan(1 + TamanhoNonce, TamanhoTag);
        var cifrado = resultado.AsSpan(1 + TamanhoNonce + TamanhoTag);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(chave, TamanhoTag);
        aes.Encrypt(nonce, texto, cifrado, tag, Proposito);
        return resultado;
    }

    private static byte[] AesGcmUnprotect(byte[] chave, ReadOnlySpan<byte> dados)
    {
        if (dados.Length < TamanhoNonce + TamanhoTag)
        {
            throw new CryptographicException("Token cifrado inválido.");
        }

        var nonce = dados[..TamanhoNonce];
        var tag = dados.Slice(TamanhoNonce, TamanhoTag);
        var cifrado = dados[(TamanhoNonce + TamanhoTag)..];
        var texto = new byte[cifrado.Length];
        using var aes = new AesGcm(chave, TamanhoTag);
        aes.Decrypt(nonce, cifrado, tag, texto, Proposito);
        return texto;
    }

    /// <summary>CryptProtectData/CryptUnprotectData com escopo de máquina (CRYPTPROTECT_LOCAL_MACHINE).</summary>
    [SupportedOSPlatform("windows")]
    private static partial class Dpapi
    {
        private const int LocalMachine = 0x4;
        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] dados, byte[] entropia) => Executar(dados, entropia, proteger: true);

        public static byte[] Unprotect(byte[] dados, byte[] entropia) => Executar(dados, entropia, proteger: false);

        private static unsafe byte[] Executar(byte[] dados, byte[] entropia, bool proteger)
        {
            fixed (byte* pDados = dados)
            fixed (byte* pEntropia = entropia)
            {
                var entrada = new DataBlob { Tamanho = dados.Length, Dados = (IntPtr)pDados };
                var extra = new DataBlob { Tamanho = entropia.Length, Dados = (IntPtr)pEntropia };
                var ok = proteger
                    ? CryptProtectData(ref entrada, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, LocalMachine | UiForbidden, out var saida)
                    : CryptUnprotectData(ref entrada, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out saida);
                if (!ok)
                {
                    throw new CryptographicException(Marshal.GetLastPInvokeError());
                }

                try
                {
                    var resultado = new byte[saida.Tamanho];
                    Marshal.Copy(saida.Dados, resultado, 0, saida.Tamanho);
                    return resultado;
                }
                finally
                {
                    LocalFree(saida.Dados);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Tamanho;
            public IntPtr Dados;
        }

        [LibraryImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptProtectData(
            ref DataBlob dados, IntPtr descricao, ref DataBlob entropia, IntPtr reservado, IntPtr prompt, int flags, out DataBlob saida);

        [LibraryImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptUnprotectData(
            ref DataBlob dados, IntPtr descricao, ref DataBlob entropia, IntPtr reservado, IntPtr prompt, int flags, out DataBlob saida);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr memoria);
    }
}
