using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CheckDraw.Api;

/// <summary>Fila assíncrona in-process (equivale ao Celery+Redis do rev_cad,
/// sem infra extra — Railway roda no mesmo container).</summary>
public class JobQueue : BackgroundService
{
    private readonly Channel<Func<Task>> _ch = Channel.CreateUnbounded<Func<Task>>();
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public Guid Enqueue(Func<Task> work) => Enqueue(_ => work());

    public Guid Enqueue(Func<JobState, Task> work)
    {
        var id = Guid.NewGuid();
        var state = new JobState { Status = "queued" };
        _jobs[id] = state;
        _ch.Writer.TryWrite(async () =>
        {
            state.Status = "running";
            try { await work(state); state.Status = "done"; }
            catch (Exception ex) { state.Status = "error"; state.Error = ex.Message; }
        });
        return id;
    }

    public JobState? Get(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IReadOnlyDictionary<Guid, JobState> All => _jobs;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var work in _ch.Reader.ReadAllAsync(ct))
            await work();
    }

    public class JobState
    {
        public string Status { get; set; } = "queued";
        public string? Progress { get; set; }
        public string? Error { get; set; }
        public object? Result { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
