using System.Reflection;

namespace IntegracaoSolidesDP.Worker.Infrastructure;

/// <summary>Versão do serviço (a do pacote: scripts/publicar-windows.ps1 -Versao), gravada em cada execução.</summary>
public static class VersaoServico
{
    public static string Atual { get; } =
        typeof(VersaoServico).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "desconhecida";
}
