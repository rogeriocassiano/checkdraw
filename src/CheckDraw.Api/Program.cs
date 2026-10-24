using System.Text.Json;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Engine.Release;
using CheckDraw.Engine.Triage;
using CheckDraw.Extractors.Dxf;
using CheckDraw.Infrastructure.Data;
using CheckDraw.Infrastructure.Import;
using CheckDraw.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
builder.Services.AddSingleton<CheckDraw.Api.JobQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CheckDraw.Api.JobQueue>());
var app = builder.Build();
var jobs = app.Services.GetRequiredService<CheckDraw.Api.JobQueue>();
app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

var dbPath = app.Configuration["CHECKDRAW_DB"] ?? "checkdraw-web.db";
using (var db = new CheckDrawDbContext(dbPath)) db.Database.EnsureCreated();
IDocumentExtractor[] extractors = { new DxfExtractor(), new OdaDwgExtractor() };

app.MapPost("/api/import", async (ImportReq r) =>
{
    var dir = r.Dir;
    if (!Directory.Exists(dir)) return Results.BadRequest("pasta não existe");
    var contract = File.Exists(Path.Combine(dir, "contrato.json"))
        ? JsonSerializer.Deserialize<ContractProfile>(await File.ReadAllTextAsync(Path.Combine(dir, "contrato.json")))!
        : new ContractProfile { Name = "Contrato padrão" };
    var master = File.Exists(Path.Combine(dir, "lista-mestra.json"))
        ? JsonSerializer.Deserialize<List<MasterListEntry>>(await File.ReadAllTextAsync(Path.Combine(dir, "lista-mestra.json")))!
        : new List<MasterListEntry>();
    var res = await new BatchImporter(extractors).ImportAsync(dir, contract, master, dbPath, r.Supplier ?? "");
    return Results.Ok(new { files = res.Batch.Files.Count, findings = res.Findings.Count, revisions = res.Revisions.Count });
});

app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    if (form.Files.Count == 0) return Results.BadRequest("nenhum arquivo");
    var dir = Path.Combine(Path.GetTempPath(), "cd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    foreach (var f in form.Files)
    {
        var path = Path.Combine(dir, Path.GetFileName(f.FileName));
        await using var s = File.Create(path);
        await f.CopyToAsync(s);
    }
    var supplier = form["supplier"].FirstOrDefault() ?? "";
    var contractName = form["contract"].FirstOrDefault() ?? "Contrato padrão";
    var jobId = jobs.Enqueue(async () =>
    {
        var contract = new ContractProfile { Name = contractName };
        await new BatchImporter(extractors).ImportAsync(dir, contract,
            new List<MasterListEntry>(), dbPath, supplier);
    });
    return Results.Ok(new { jobId, status = "queued" });
});

app.MapGet("/api/jobs/{id:guid}", (Guid id) =>
{
    var j = jobs.Get(id);
    return j == null ? Results.NotFound() : Results.Ok(new { j.Status, j.Error });
});

app.MapGet("/api/queue", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    if (batch == null) return Results.Ok(Array.Empty<object>());
    var findings = await db.Findings.ToListAsync();
    return Results.Ok(RiskScorer.OrderQueue(batch, findings).Select(x => new
    {
        x.File.FileName, code = x.File.Identity.Code, rev = x.File.Identity.Revision,
        health = x.File.Health.ToString(), id = x.File.Identity.Status.ToString(), x.Score
    }));
});

app.MapGet("/api/findings", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.Findings.OrderByDescending(f => f.Severity).ToListAsync());
});

app.MapGet("/api/marks/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.CheckMarks.Where(c => c.FileName == file).ToListAsync());
});

app.MapPost("/api/marks", async (CheckMark m) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    m.Id = Guid.NewGuid(); m.CreatedAt = DateTime.UtcNow;
    db.CheckMarks.Add(m);
    await db.SaveChangesAsync();
    return Results.Ok(m.Id);
});

app.MapPost("/api/release", async (ReleaseReq r) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var revs = await db.Revisions.Where(x => x.Code == r.Code).ToListAsync();
    var attempt = ReleaseControl.Evaluate(r.Code, r.Rev, r.Purpose, revs, "web");
    db.ReleaseAttempts.Add(attempt);
    await db.SaveChangesAsync();
    return Results.Ok(attempt);
});

app.MapPost("/api/transfer", async (TransferReq r) =>
{
    var svc = new RevisionTransferService(extractors);
    var res = await svc.TransferAsync(dbPath, r.Code, r.From, r.To);
    return res.Error != null ? Results.BadRequest(res.Error) : Results.Ok(res);
});

// Veredito do revisor por apontamento → status + review + feedback pair
app.MapPost("/api/findings/{id:guid}/review", async (Guid id, ReviewReq r) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Findings.FindAsync(id);
    if (f == null) return Results.NotFound();
    f.ReviewStatus = Enum.Parse<ReviewStatus>(r.Action, true);
    db.FindingReviews.Add(new FindingReview
    {
        FindingId = id, Action = r.Action.ToLowerInvariant(),
        Comment = r.Comment, CorrectedValue = r.CorrectedValue, Reviewer = r.Reviewer
    });
    db.AiFeedbackPairs.Add(new AiFeedbackPair
    {
        FileName = f.FileName, ChecklistCode = f.RuleId,
        AiOutput = f.Description + (f.Evidence != null ? $" ({f.Evidence})" : ""),
        HumanAction = r.Action.ToLowerInvariant(),
        HumanCorrection = r.CorrectedValue,
        WasCorrect = r.Action.Equals("confirmed", StringComparison.OrdinalIgnoreCase),
        Reviewer = r.Reviewer
    });
    db.AuditLog.Add(new AuditEvent { Action = "review", Detail = $"{f.RuleId} em {f.FileName}: {r.Action}", User = r.Reviewer });
    await db.SaveChangesAsync();
    return Results.Ok(new { f.ReviewStatus });
});

// Score de conformidade ponderado por arquivo (critical=3, warning=2, info=1)
app.MapGet("/api/score/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var fs = await db.Findings.Where(f => f.FileName == file).ToListAsync();
    var open = fs.Where(f => f.ReviewStatus != ReviewStatus.Dismissed).ToList();
    var penalty = open.Sum(f => f.Severity == FindingSeverity.Critical ? 15
        : f.Severity == FindingSeverity.Error ? 10
        : f.Severity == FindingSeverity.Warning ? 5 : 2);
    return Results.Ok(new
    {
        file, score = Math.Max(0, 100 - penalty), total = fs.Count,
        critical = open.Count(f => f.Severity == FindingSeverity.Critical),
        pending = fs.Count(f => f.ReviewStatus == ReviewStatus.Open)
    });
});

// Checklist: CRUD dos critérios + status por arquivo (ok/apontamento/pendente)
app.MapGet("/api/checklist", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.ChecklistItems.ToListAsync());
});

app.MapPost("/api/checklist", async (ChecklistItem item) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    item.Id = Guid.NewGuid();
    db.ChecklistItems.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(item.Id);
});

app.MapGet("/api/checklist/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var items = await db.ChecklistItems.ToListAsync();
    var findings = await db.Findings.Where(f => f.FileName == file).ToListAsync();
    return Results.Ok(items.Select(i => new
    {
        i.Code, i.Category, i.Description, severity = i.Severity.ToString(),
        status = findings.Any(f => f.RuleId == i.Code) ? "finding" : "ok",
        findings = findings.Where(f => f.RuleId == i.Code).Select(f => f.Description)
    }));
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", db = dbPath.Contains("Host=") ? "postgres" : "sqlite" }));

app.Run();

record ReviewReq(string Action, string? Comment, string? CorrectedValue, string? Reviewer);
record ImportReq(string Dir, string? Supplier);
record ReleaseReq(string Code, string Rev, ReleasePurpose Purpose);
record TransferReq(string Code, string From, string To);
