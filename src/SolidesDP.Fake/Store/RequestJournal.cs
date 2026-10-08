using System.Text.Json;
using System.Text.Json.Nodes;
using SolidesDP.Fake.Domain;

namespace SolidesDP.Fake.Store;

/// <summary>Diario de requests (nao-admin), com numeracao sequencial.</summary>
internal sealed class RequestJournal(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _entries = [];
    private long _seq;
    private int _generation;

    /// <summary>Registra a chegada do request (atribui o <c>seq</c>); a entrada so aparece em <see cref="Snapshot"/> ao completar.</summary>
    public JournalScope Begin(string method, string path, string? query, string? body)
    {
        lock (_gate)
        {
            return new JournalScope(this, ++_seq, _generation, method, path, query, ParseBody(body), time.GetUtcNow());
        }
    }

    public IReadOnlyList<RecordedRequest> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.OrderBy(e => e.Seq)];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _seq = 0;
            _generation++;
        }
    }

    private void Complete(JournalScope scope, int status, string? fault)
    {
        lock (_gate)
        {
            if (scope.Generation != _generation)
            {
                return;
            }

            _entries.Add(new RecordedRequest
            {
                Seq = scope.Seq,
                Method = scope.Method,
                Path = scope.Path,
                Query = scope.Query,
                Body = scope.Body,
                Status = status,
                At = scope.At,
                Fault = fault,
            });
        }
    }

    private static JsonNode? ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    /// <summary>Request em andamento no diario. <see cref="Complete"/> e idempotente.</summary>
    internal sealed class JournalScope(
        RequestJournal owner,
        long seq,
        int generation,
        string method,
        string path,
        string? query,
        JsonNode? body,
        DateTimeOffset at)
    {
        private int _completed;

        public const string ItemKey = "fake.journal.scope";

        public long Seq { get; } = seq;

        public int Generation { get; } = generation;

        public string Method { get; } = method;

        public string Path { get; } = path;

        public string? Query { get; } = query;

        public JsonNode? Body { get; } = body;

        public DateTimeOffset At { get; } = at;

        /// <summary>Falha injetada neste request (para o diario).</summary>
        public string? Fault { get; set; }

        public void Complete(int status)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                owner.Complete(this, status, Fault);
            }
        }
    }
}
