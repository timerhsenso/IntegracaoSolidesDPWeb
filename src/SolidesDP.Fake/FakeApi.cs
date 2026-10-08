namespace SolidesDP.Fake;

/// <summary>
/// Tipo marcador do assembly do fake. Os testes (inclusive de outros projetos) devem usar
/// <c>WebApplicationFactory&lt;SolidesDP.Fake.FakeApi&gt;</c>: o <c>Program</c> do fake e interno e,
/// de qualquer forma, ficaria ambiguo ao lado do <c>Program</c> global do worker.
/// </summary>
public sealed class FakeApi
{
    private FakeApi()
    {
    }
}
