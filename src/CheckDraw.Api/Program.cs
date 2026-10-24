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
var app = builder.Build();
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
    var contract = new ContractProfile { Name = form["contract"].FirstOrDefault() ?? "Contrato padrão" };
    var res = await new BatchImporter(extractors).ImportAsync(dir, contract,
        new List<MasterListEntry>(), dbPath, form["supplier"].FirstOrDefault() ?? "");
    return Results.Ok(new { files = res.Batch.Files.Count, findings = res.Findings.Count, revisions = res.Revisions.Count });
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

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", db = dbPath.Contains("Host=") ? "postgres" : "sqlite" }));

app.Run();

record ImportReq(string Dir, string? Supplier);
record ReleaseReq(string Code, string Rev, ReleasePurpose Purpose);
record TransferReq(string Code, string From, string To);
