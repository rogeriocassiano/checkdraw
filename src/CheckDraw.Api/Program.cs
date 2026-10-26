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
var supaUrl = app.Configuration["SUPABASE_URL"];   // https://<ref>.supabase.co
var supaKey = app.Configuration["SUPABASE_KEY"];   // publishable/service key
var storageOk = supaUrl != null && supaKey != null;
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
    var fileNames = form.Files.Select(f => Path.GetFileName(f.FileName)).ToList();
    var jobId = jobs.Enqueue(async () =>
    {
        var contract = new ContractProfile { Name = contractName };
        var res = await new BatchImporter(extractors).ImportAsync(dir, contract,
            new List<MasterListEntry>(), dbPath, supplier);

        if (storageOk)
        {
            using var http = new HttpClient();
            await using var db = new CheckDrawDbContext(dbPath);
            var batchId = res.Batch.Id.ToString("N")[..8];
            foreach (var f in form.Files)
            {
                var name = Path.GetFileName(f.FileName);
                var sp = $"{batchId}/{name}";
                using var content = new StreamContent(File.OpenRead(Path.Combine(dir, name)));
                content.Headers.ContentType = new("application/octet-stream");
                var url = $"{supaUrl}/storage/v1/object/drawings/{sp}";
                using var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                r.Headers.Add("apikey", supaKey);
                r.Headers.Add("Authorization", $"Bearer {supaKey}");
                r.Headers.Add("x-upsert", "true");
                var resp = await http.SendAsync(r);
                if (resp.IsSuccessStatusCode)
                {
                    var pub = $"{supaUrl}/storage/v1/object/public/drawings/{sp}";
                    var ef = await db.Files.FirstOrDefaultAsync(x => x.FileName == name);
                    if (ef != null) { ef.StoragePath = pub; }
                }
            }
            await db.SaveChangesAsync();
        }
    });
    return Results.Ok(new { jobId, status = "queued", storage = storageOk });
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
        health = x.File.Health.ToString(), id = x.File.Identity.Status.ToString(), x.Score,
        storage = x.File.StoragePath
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

// Análise de desenho por IA: imagem → findings com bbox + correção sugerida
app.MapPost("/api/analyze", async (HttpRequest req) =>
{
    var ai = new CheckDraw.Infrastructure.Ai.GeminiAnalyzer();
    if (!ai.Available) return Results.BadRequest("GEMINI_API_KEY não configurada");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest("imagem obrigatória");
    var fileName = form["fileName"].FirstOrDefault() ?? file.FileName;

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);

    await using var db = new CheckDrawDbContext(dbPath);
    var items = await db.ChecklistItems.Where(i => i.AiCheckable).ToListAsync();
    var codes = items.Select(i => i.Code).ToList();
    var feedback = await db.AiFeedbackPairs
        .Where(p => codes.Contains(p.ChecklistCode))
        .OrderByDescending(p => p.CreatedAt).Take(30).ToListAsync();

    var findings = await ai.AnalyzeChecklistAsync(ms.ToArray(), fileName, items, feedback);

    // ID-FILE determinístico: número do arquivo DEVE ser idêntico ao carimbo
    var expected = System.Text.RegularExpressions.Regex
        .Match(fileName, @"^(.*?)(?:\s*-DC|\s*-.*|\..*)?$").Groups[1].Value;
    var carimbo = ai.CarimboCode;
    if (!string.IsNullOrEmpty(carimbo) &&
        !string.Equals(expected.Trim(), carimbo.Trim(), StringComparison.OrdinalIgnoreCase))
        findings.Add(new Finding
        {
            RuleId = "ID-FILE", FileName = fileName,
            Description = $"Código do carimbo '{carimbo}' diverge do nome do arquivo '{expected}' — possível arquivo com nome errado",
            Evidence = $"detectado: {carimbo}", Severity = FindingSeverity.Critical,
            Source = FindingSource.Rule, Kind = FindingKind.Objective
        });

    db.Findings.AddRange(findings);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "ai-analyze", User = "gemini",
        Detail = $"{fileName}: {findings.Count} apontamentos (checklist {items.Count} itens, {feedback.Count} feedbacks no contexto)"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { fileName, findings = findings.Count });
});

// Checklist: instanciar template num contrato (copia os itens)
app.MapPost("/api/checklist/instantiate", async (InstantiateReq r) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var items = await db.ChecklistItems
        .Where(i => i.ChecklistTemplateId == r.TemplateId).ToListAsync();
    foreach (var i in items)
        db.ChecklistItems.Add(new ChecklistItem
        {
            ContractProfileId = r.ContractProfileId,
            Code = i.Code, Category = i.Category, Description = i.Description,
            Expected = i.Expected, Severity = i.Severity,
            AiCheckable = i.AiCheckable, AiPromptHint = i.AiPromptHint
        });
    await db.SaveChangesAsync();
    return Results.Ok(new { cloned = items.Count });
});

// DWG via APS: upload → tradução SVF2 → URN para o viewer no browser
app.MapPost("/api/aps/translate", async (HttpRequest req) =>
{
    var aps = new CheckDraw.Infrastructure.Aps.ApsService();
    if (!aps.Available) return Results.BadRequest("APS_CLIENT_ID/SECRET não configurados");
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest("arquivo obrigatório");
    var name = Path.GetFileName(file.FileName);

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var bytes = ms.ToArray();

    // registra o arquivo no lote — aparece na fila/apontamentos
    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    if (batch == null)
    {
        var cp = await db.Contracts.FirstOrDefaultAsync() ?? new ContractProfile { Name = "Padrão" };
        if (cp.Id == Guid.Empty || !db.Contracts.Local.Any()) db.Contracts.Add(cp);
        batch = new Batch { Supplier = "upload", Contract = cp };
        db.Batches.Add(batch);
    }
    var drawingFile = new DrawingFile
    {
        FileName = name, FullPath = $"aps-oss:{name}", Extension = Path.GetExtension(name),
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        SizeBytes = bytes.Length, Health = FileHealth.Ok
    };
    batch.Files.Add(drawingFile);
    await db.SaveChangesAsync();

    var jobId = jobs.Enqueue(async () =>
    {
        var urn = await aps.UploadAsync("checkdraw_dwg", $"{Guid.NewGuid():N}_{name}", bytes);
        await aps.TranslateAndWaitAsync(urn);
        await using var db2 = new CheckDrawDbContext(dbPath);
        var f = await db2.Files.FirstOrDefaultAsync(x => x.FileName == name);
        if (f != null) { f.StoragePath = $"aps:{urn}"; await db2.SaveChangesAsync(); }
    });
    return Results.Ok(new { jobId, name, status = "queued" });
});

// Lookup: storage path por nome de arquivo (viewer encontra PDFs de qualquer lote)
app.MapGet("/api/files/{name}", async (string name) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Files.Where(x => x.FileName == name)
        .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
    return f == null ? Results.NotFound() : Results.Ok(new { f.FileName, f.StoragePath });
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
record InstantiateReq(Guid TemplateId, Guid ContractProfileId);
record ImportReq(string Dir, string? Supplier);
record ReleaseReq(string Code, string Rev, ReleasePurpose Purpose);
record TransferReq(string Code, string From, string To);
