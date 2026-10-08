using SolidesDP.Fake.Configuration;

namespace SolidesDP.Fake.Store;

/// <summary>Regras de falha ativas. O consumo e atomico: cada request casa no maximo uma regra.</summary>
internal sealed class FaultRegistry
{
    private readonly Lock _gate = new();
    private readonly List<ActiveFaultRule> _rules = [];

    public void Add(FaultRule rule)
    {
        lock (_gate)
        {
            _rules.Add(new ActiveFaultRule { Rule = rule with { }, Remaining = rule.Times });
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    public IReadOnlyList<ActiveFaultRule> Snapshot()
    {
        lock (_gate)
        {
            return [.. _rules.Select(r => new ActiveFaultRule { Rule = r.Rule with { }, Remaining = r.Remaining })];
        }
    }

    /// <summary>Procura (na ordem de cadastro) a primeira regra que casa e ainda tem consumos, e consome um.</summary>
    public FaultRule? TryConsume(string method, string path)
    {
        lock (_gate)
        {
            foreach (var active in _rules)
            {
                if (active.Remaining == 0 || !Matches(active.Rule, method, path))
                {
                    continue;
                }

                if (active.Remaining > 0)
                {
                    active.Remaining--;
                }

                var consumed = active.Rule;
                _rules.RemoveAll(r => r.Remaining == 0);
                return consumed;
            }

            return null;
        }
    }

    private static bool Matches(FaultRule rule, string method, string path)
    {
        if (rule.Method is { Length: > 0 } m && !string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pattern = rule.Path;
        return pattern.EndsWith('*')
            ? path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(Trim(path), Trim(pattern), StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value) => value.Length > 1 ? value.TrimEnd('/') : value;
}
