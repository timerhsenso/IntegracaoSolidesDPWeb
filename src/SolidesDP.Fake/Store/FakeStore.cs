using Microsoft.Extensions.Options;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;

namespace SolidesDP.Fake.Store;

/// <summary>
/// Store em memoria com um unico lock. Toda leitura/escrita de dados acontece em <see cref="Execute{T}"/>;
/// o <see cref="Behavior"/> corrente tambem vive aqui.
/// </summary>
internal sealed class FakeStore
{
    private readonly Lock _gate = new();
    private readonly FakeOptions _options;
    private StoreData _data;
    private FakeBehavior _behavior;

    public FakeStore(IOptions<FakeOptions> options)
    {
        _options = options.Value;
        _data = StoreData.From(SeedLoader.Load(_options));
        _behavior = _options.Behavior with { };
    }

    /// <summary>Perfil de comportamento corrente. Instancias nunca sao mutadas depois de publicadas.</summary>
    public FakeBehavior Behavior
    {
        get
        {
            lock (_gate)
            {
                return _behavior;
            }
        }
        set
        {
            lock (_gate)
            {
                _behavior = value with { };
            }
        }
    }

    public IReadOnlyList<string> Tokens => _options.EffectiveTokens;

    /// <summary>Executa <paramref name="work"/> segurando o lock. O delegate nao pode vazar objetos mutaveis.</summary>
    public T Execute<T>(Func<StoreData, T> work)
    {
        lock (_gate)
        {
            return work(_data);
        }
    }

    /// <summary>Recarrega o seed e restaura o comportamento padrao da configuracao.</summary>
    public void Reset()
    {
        var fresh = StoreData.From(SeedLoader.Load(_options));
        lock (_gate)
        {
            _data = fresh;
            _behavior = _options.Behavior with { };
        }
    }

    public FakeState Snapshot() => Execute(data => data.ToState());
}
