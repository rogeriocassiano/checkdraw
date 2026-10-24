using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CheckDraw.Api;

/// <summary>Fila assíncrona in-process (equivale ao Celery+Redis do rev_cad,
/// sem infra extra — Railway roda no mesmo container).</summary>
public class JobQueue : BackgroundService
{
    private readonly Channel<Func<Task>> _ch = Channel.CreateUnbounded<Func<Task>>();
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public Guid Enqueue(Func<Task> work)
    {
        var id = Guid.NewGuid();
        _jobs[id] = new JobState { Status = "queued" };
        _ch.Writer.TryWrite(async () =>
        {
            _jobs[id].Status = "running";
            try { await work(); _jobs[id].Status = "done"; }
            catch (Exception ex) { _jobs[id].Status = "error"; _jobs[id].Error = ex.Message; }
        });
        return id;
    }

    public JobState? Get(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var work in _ch.Reader.ReadAllAsync(ct))
            await work();
    }

    public class JobState { public string Status = "queued"; public string? Error; }
}
