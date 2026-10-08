using System.Reflection;
using System.Text.Json;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;

namespace SolidesDP.Fake.Store;

/// <summary>Carrega o seed (arquivo configurado em <c>Fake:SeedFile</c> ou o recurso embutido).</summary>
internal static class SeedLoader
{
    private const string EmbeddedSeedName = "fake-seed.json";

    public static FakeState Load(FakeOptions options)
    {
        using var stream = OpenSeed(options);
        try
        {
            return JsonSerializer.Deserialize<FakeState>(stream, FakeJson.Options)
                ?? throw new InvalidOperationException("O arquivo de seed esta vazio.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Seed invalido ({options.SeedFile ?? EmbeddedSeedName}): {ex.Message}", ex);
        }
    }

    private static Stream OpenSeed(FakeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SeedFile))
        {
            return File.OpenRead(options.SeedFile);
        }

        return Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedSeedName)
            ?? throw new InvalidOperationException($"Recurso embutido '{EmbeddedSeedName}' nao encontrado.");
    }
}
