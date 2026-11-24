using System.Text.Json;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Engine.Identification;
using CheckDraw.Engine.Release;
using CheckDraw.Engine.Rules;
using CheckDraw.Engine.Triage;
using CheckDraw.Extractors.Dxf;
using CheckDraw.Infrastructure.Ai;
using CheckDraw.Infrastructure.Data;
using CheckDraw.Infrastructure.Import;
using CheckDraw.Infrastructure.Pdf;
using CheckDraw.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
builder.Services.AddSingleton<CheckDraw.Api.JobQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CheckDraw.Api.JobQueue>());
var app = builder.Build();
var jobs = app.Services.GetRequiredService<CheckDraw.Api.JobQueue>();
app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

var webDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "web"));
if (Directory.Exists(webDir))
{
    var fp = new PhysicalFileProvider(webDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
}

var dbPath = app.Configuration["CHECKDRAW_DB"] ?? "checkdraw-web.db";
var supaUrl = app.Configuration["SUPABASE_URL"];   // https://<ref>.supabase.co
var supaKey = app.Configuration["SUPABASE_KEY"];   // publishable/service key
var storageOk = !string.IsNullOrWhiteSpace(supaUrl) && !string.IsNullOrWhiteSpace(supaKey);

try
{
    using var db = new CheckDrawDbContext(dbPath);
    db.Database.EnsureCreated();
    EnsureDefaultChecklist(db);
}
catch (Exception ex) when (dbPath.Contains("Host="))
{
    Console.WriteLine($"[WARN] Postgres indisponível ({ex.Message}); ativando fallback SQLite local checkdraw-web.db");
    dbPath = "checkdraw-web.db";
    storageOk = false;
    using var db = new CheckDrawDbContext(dbPath);
    db.Database.EnsureCreated();
    EnsureDefaultChecklist(db);
}

IDocumentExtractor[] extractors = { new DxfExtractor(), new OdaDwgExtractor(), new VectorPdfExtractor() };

static ChecklistItem[] DefaultChecklistItems() => new[]
{
    new ChecklistItem
    {
        Code = "CAR-01", Category = "carimbo", Severity = FindingSeverity.Error, AiCheckable = true,
        Description = "Campos obrigatórios do carimbo preenchidos e legíveis (DESENHO Nº, TÍTULO, ESCALA, FORMATO, RESPONSÁVEIS, DATA)",
        AiPromptHint = "Verifique no canto inferior direito se há campos obrigatórios em branco, truncados ou contraditórios no carimbo."
    },
    new ChecklistItem
    {
        Code = "REV-TAB", Category = "revisão", Severity = FindingSeverity.Error, AiCheckable = true,
        Description = "Tabela de revisões coerente com a revisão atual do carimbo (descrição da revisão, data e responsável preenchidos)",
        AiPromptHint = "Compare a maior letra/número da tabela de revisões com o campo REV do carimbo e verifique se faltam data/descrição/assinatura."
    },
    new ChecklistItem
    {
        Code = "BOM-01", Category = "materiais", Severity = FindingSeverity.Error, AiCheckable = true,
        Description = "Lista de Materiais / Quadro de Aplicação com colunas completas (Item/Marca, Qtd, DN/Dimensão, Descrição, Material, Peso)",
        AiPromptHint = "Inspecione cada linha da tabela de materiais/aplicação buscando células vazias, quantidades zeradas, materiais omitidos ou pesos incoerentes."
    },
    new ChecklistItem
    {
        Code = "BAL-01", Category = "chamadas", Severity = FindingSeverity.Critical, AiCheckable = true,
        Description = "Correspondência biunívoca entre balões/marcas nas vistas e linhas da Lista de Materiais / Quadro de Aplicação",
        AiPromptHint = "Confira todos os balões/posições/marcas apontados nas vistas: aponte item chamado na vista que não existe na tabela ou linha da tabela sem chamada no desenho."
    },
    new ChecklistItem
    {
        Code = "COT-01", Category = "cotagem", Severity = FindingSeverity.Warning, AiCheckable = true,
        Description = "Cotas principais, furações, entre-eixos e elevações legíveis, sem sobreposição, ambiguidade ou conflito entre vistas",
        AiPromptHint = "Procure cotas sobrepostas a linhas/textos, somas de cotas parciais divergindo da cota total, furos sem diâmetro/entre-eixos ou vistas contraditórias."
    },
    new ChecklistItem
    {
        Code = "TOL-01", Category = "fabricação", Severity = FindingSeverity.Warning, AiCheckable = true,
        Description = "Simbologia de solda, acabamento superficial e tolerâncias dimensionais/geométricas completas onde exigido",
        AiPromptHint = "Verifique símbolos de solda sem garganta/cateto quando necessário, uniões soldadas sem indicação e ajustes sem tolerância."
    },
    new ChecklistItem
    {
        Code = "MAT-01", Category = "notas", Severity = FindingSeverity.Warning, AiCheckable = true,
        Description = "Notas gerais, especificação de material, pintura/proteção anticorrosiva e normas aplicáveis declaradas",
        AiPromptHint = "Verifique na região de Notas Gerais se há referências truncadas ('XXXX', 'TBD', '?') ou ausência de norma/tratamento superficial."
    },
    new ChecklistItem
    {
        Code = "MRC-01", Category = "conferência", Severity = FindingSeverity.Critical, AiCheckable = true,
        Description = "Marcações manuais de correção (anotações em vermelho, rabiscos, setas, nuvens de revisão) pendentes na folha",
        AiPromptHint = "Reporte individualmente cada anotação manuscrita/vermelha de conferência com bbox preciso e transcreva o que foi escrito."
    }
};

static int EnsureDefaultChecklist(CheckDrawDbContext db, bool onlyIfEmpty = true)
{
    var existing = db.ChecklistItems.Select(i => i.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (onlyIfEmpty && existing.Count > 0) return 0;
    var added = 0;
    foreach (var item in DefaultChecklistItems())
    {
        if (existing.Contains(item.Code)) continue;
        db.ChecklistItems.Add(item);
        added++;
    }
    if (added > 0) db.SaveChanges();
    return added;
}

static async Task<byte[]?> LoadFileBytesAsync(DrawingFile? f)
{
    if (f == null) return null;
    if (!string.IsNullOrEmpty(f.StoragePath) &&
        (f.StoragePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         f.StoragePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(f.StoragePath);
    }
    if (!string.IsNullOrEmpty(f.FullPath) && File.Exists(f.FullPath))
        return await File.ReadAllBytesAsync(f.FullPath);
    return null;
}

static async Task<(int totalFindings, string? carimboCode, string? error)> AnalyzeAndPersistAsync(
    CheckDrawDbContext db, byte[] bytes, string fileName)
{
    var ai = new GeminiAnalyzer();
    if (!ai.Available) return (0, null, "GEMINI_API_KEY não configurada");

    var items = await db.ChecklistItems.Where(i => i.AiCheckable).ToListAsync();
    var codes = items.Select(i => i.Code).ToList();
    var feedback = await db.AiFeedbackPairs
        .Where(p => p.ChecklistCode != null && codes.Contains(p.ChecklistCode))
        .OrderByDescending(p => p.CreatedAt).Take(30).ToListAsync();

    // Limpa achados de IA abertos anteriores e ID-FILE anterior deste arquivo (preserva revisados pelo humano)
    var stale = await db.Findings
        .Where(f => f.FileName == fileName &&
                    ((f.Source == FindingSource.Ai && f.ReviewStatus == ReviewStatus.Open) ||
                     f.RuleId == "ID-FILE"))
        .ToListAsync();
    if (stale.Count > 0) db.Findings.RemoveRange(stale);

    var findings = await ai.AnalyzeChecklistAsync(bytes, fileName, items, feedback);
    if (findings.Count == 0 && ai.CarimboCode == null && ai.LastError != null)
        return (0, null, ai.LastError);

    // ID-FILE determinístico: código do desenho no nome DEVE ser idêntico ao carimbo
    var expected = TitleBlockIdentifier.ExtractCodeFromFileName(fileName);
    var carimbo = ai.CarimboCode?.Trim();
    if (!string.IsNullOrEmpty(carimbo) && !string.IsNullOrEmpty(expected) &&
        !string.Equals(expected.Trim(), carimbo, StringComparison.OrdinalIgnoreCase))
    {
        findings.Add(new Finding
        {
            RuleId = "ID-FILE", FileName = fileName,
            DocumentCode = carimbo, Revision = ai.CarimboRevision,
            Description = $"Código do carimbo '{carimbo}' diverge do nome do arquivo '{expected}' — possível arquivo com nome errado",
            Evidence = $"detectado no carimbo: {carimbo}; esperado pelo arquivo: {expected}",
            Severity = FindingSeverity.Critical,
            Source = FindingSource.Rule, Kind = FindingKind.Objective
        });
    }

    // Atualiza identidade do arquivo e revisão quando a IA leu o carimbo
    var ef = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == fileName);
    if (ef != null && !string.IsNullOrEmpty(carimbo))
    {
        if (string.IsNullOrEmpty(ef.Identity.Code) || ef.Identity.Status == IdentificationStatus.Unidentified)
        {
            ef.Identity.Code = carimbo.ToUpperInvariant();
            if (!string.IsNullOrEmpty(ai.CarimboRevision)) ef.Identity.Revision = ai.CarimboRevision.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(ai.CarimboTitle)) ef.Identity.Title = ai.CarimboTitle.Trim();
            ef.Identity.Status = IdentificationStatus.AiSuggested;
            ef.Identity.MatchedProfile = "gemini-carimbo";
            ef.Identity.EvidenceText = $"Carimbo lido por IA: {carimbo}" + (ai.CarimboRevision != null ? $" Rev.{ai.CarimboRevision}" : "");
        }
        ef.Lifecycle = DrawingLifecycle.Analyzed;

        if (!TitleBlockIdentifier.IsCheckedScan(fileName))
        {
            var revCode = ef.Identity.Code ?? carimbo.ToUpperInvariant();
            var revVal = ef.Identity.Revision ?? ai.CarimboRevision?.Trim().ToUpperInvariant() ?? "—";
            var hasRev = await db.Revisions.AnyAsync(r => r.Code == revCode && r.Revision == revVal);
            if (!hasRev)
                db.Revisions.Add(new DocumentRevision
                {
                    BatchId = ef.BatchId, Code = revCode, Revision = revVal, SourceFileId = ef.Id
                });
        }
    }

    db.Findings.AddRange(findings);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "ai-analyze", User = "gemini",
        Detail = $"{fileName}: {findings.Count} apontamentos (carimbo={carimbo ?? "n/d"}, checklist {items.Count} itens, {feedback.Count} feedbacks)"
    });
    await db.SaveChangesAsync();
    return (findings.Count, carimbo, null);
}

static ContractProfile BuildDefaultContract(string name) => new()
{
    Name = name,
    TitleBlocks = new()
    {
        new TitleBlockProfile
        {
            Name = "Carimbo padrão A", BlockName = "TITULO_A",
            AttributeMap = new() { ["code"] = "CODIGO", ["revision"] = "REV", ["title"] = "TITULO", ["discipline"] = "DISCIPLINA", ["format"] = "FORMATO" }
        },
        new TitleBlockProfile
        {
            Name = "Carimbo padrão B", BlockName = "TITULO_B",
            AttributeMap = new() { ["code"] = "NUMERO", ["revision"] = "REVISAO", ["title"] = "DESCRICAO" }
        },
        new TitleBlockProfile
        {
            Name = "Carimbo industrial", BlockName = "CARIMBO",
            AttributeMap = new() { ["code"] = "DESENHO", ["revision"] = "REV", ["title"] = "TITULO" }
        }
    }
};

app.MapPost("/api/import", async (ImportReq r) =>
{
    var dir = r.Dir;
    if (!Directory.Exists(dir)) return Results.BadRequest("pasta não existe");
    var contract = File.Exists(Path.Combine(dir, "contrato.json"))
        ? JsonSerializer.Deserialize<ContractProfile>(await File.ReadAllTextAsync(Path.Combine(dir, "contrato.json")))!
        : BuildDefaultContract("Contrato padrão");
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
    var fileNames = new List<string>();
    foreach (var f in form.Files)
    {
        var name = Path.GetFileName(f.FileName);
        var path = Path.Combine(dir, name);
        await using var s = File.Create(path);
        await f.CopyToAsync(s);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fileNames.Add(name);
    }
    var supplier = form["supplier"].FirstOrDefault() ?? "";
    var contractName = form["contract"].FirstOrDefault() ?? "Contrato padrão";

    var jobId = jobs.Enqueue(async state =>
    {
        state.Progress = $"Importando {fileNames.Count} arquivo(s)...";
        await using var mdb = new CheckDrawDbContext(dbPath);
        var masterPath = Path.Combine(dir, "lista-mestra.json");
        var master = File.Exists(masterPath)
            ? JsonSerializer.Deserialize<List<MasterListEntry>>(await File.ReadAllTextAsync(masterPath))!
            : await mdb.MasterList.AsNoTracking().ToListAsync();
        var contractPath = Path.Combine(dir, "contrato.json");
        var contract = File.Exists(contractPath)
            ? JsonSerializer.Deserialize<ContractProfile>(await File.ReadAllTextAsync(contractPath))!
            : BuildDefaultContract(contractName);
        var res = await new BatchImporter(extractors).ImportAsync(dir, contract, master, dbPath, supplier);

        await using var db = new CheckDrawDbContext(dbPath);
        var batchFileIds = res.Batch.Files.Select(x => x.Id).ToHashSet();
        var dbFiles = await db.Files.Where(x => batchFileIds.Contains(x.Id)).ToListAsync();

        if (storageOk)
        {
            using var http = new HttpClient();
            var batchId = res.Batch.Id.ToString("N")[..8];
            var idx = 0;
            foreach (var name in fileNames)
            {
                idx++;
                state.Progress = $"Enviando para Storage ({idx}/{fileNames.Count}): {name}";
                var localPath = Path.Combine(dir, name);
                if (!File.Exists(localPath)) continue;
                var sp = $"{batchId}/{Uri.EscapeDataString(name)}";
                await using var fs = File.OpenRead(localPath);
                using var content = new StreamContent(fs);
                content.Headers.ContentType = new("application/octet-stream");
                var url = $"{supaUrl}/storage/v1/object/drawings/{sp}";
                using var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                r.Headers.Add("apikey", supaKey);
                r.Headers.Add("Authorization", $"Bearer {supaKey}");
                r.Headers.Add("x-upsert", "true");
                var ef = dbFiles.FirstOrDefault(x => x.FileName == name);
                try
                {
                    var resp = await http.SendAsync(r);
                    if (resp.IsSuccessStatusCode)
                    {
                        var pub = $"{supaUrl}/storage/v1/object/public/drawings/{sp}";
                        if (ef != null) ef.StoragePath = pub;
                    }
                    else if (ef != null)
                        ef.StoragePath = $"/api/raw/{Uri.EscapeDataString(ef.FileName)}";
                }
                catch
                {
                    if (ef != null) ef.StoragePath = $"/api/raw/{Uri.EscapeDataString(ef.FileName)}";
                }
            }
            await db.SaveChangesAsync();
        }
        else
        {
            foreach (var ef in dbFiles)
                ef.StoragePath = $"/api/raw/{Uri.EscapeDataString(ef.FileName)}";
            await db.SaveChangesAsync();
        }

        state.Progress = "Concluído";
        state.Result = new
        {
            files = res.Batch.Files.Count,
            findings = res.Findings.Count,
            revisions = res.Revisions.Count
        };
    });
    return Results.Ok(new { jobId, filesCount = fileNames.Count, status = "queued", storage = storageOk });
});

// Serve arquivo bruto em dev local (ou proxy quando sem URL pública direta)
app.MapGet("/api/raw/{name}", async (string name) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == name);
    if (f == null) return Results.NotFound();
    if (!string.IsNullOrEmpty(f.FullPath) && File.Exists(f.FullPath))
    {
        var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
        var mime = ext == ".pdf" ? "application/pdf" : ext == ".png" ? "image/png" : "text/plain";
        return Results.File(f.FullPath, mime, enableRangeProcessing: true);
    }
    if (!string.IsNullOrEmpty(f.StoragePath) && f.StoragePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return Results.Redirect(f.StoragePath);
    return Results.NotFound();
});

app.MapGet("/api/jobs/{id:guid}", (Guid id) =>
{
    var j = jobs.Get(id);
    return j == null ? Results.NotFound() : Results.Ok(new { j.Status, j.Progress, j.Error, j.Result });
});

app.MapGet("/api/queue", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    if (batch == null) return Results.Ok(Array.Empty<object>());
    var findings = await db.Findings.ToListAsync();
    var marks = await db.CheckMarks.ToListAsync();
    var links = await db.DrawingLinks.ToListAsync();

    return Results.Ok(RiskScorer.OrderQueue(batch, findings).Select(x =>
    {
        var ff = findings.Where(f => f.FileName == x.File.FileName && f.ReviewStatus != ReviewStatus.Dismissed).ToList();
        var penalty = ff.Sum(f => f.Severity == FindingSeverity.Critical ? 15
            : f.Severity == FindingSeverity.Error ? 10
            : f.Severity == FindingSeverity.Warning ? 5 : 2);
        var fm = marks.Where(m => m.FileName == x.File.FileName).ToList();
        return new
        {
            x.File.FileName,
            code = x.File.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(x.File.FileName),
            rev = x.File.Identity.Revision,
            title = x.File.Identity.Title,
            health = x.File.Health.ToString(),
            id = x.File.Identity.Status.ToString(),
            lifecycle = x.File.Lifecycle.ToString(),
            isCheckedScan = TitleBlockIdentifier.IsCheckedScan(x.File.FileName),
            isAps = x.File.StoragePath?.StartsWith("aps:") == true,
            x.Score,
            conformityScore = Math.Max(0, 100 - penalty),
            openFindings = findings.Count(f => f.FileName == x.File.FileName && f.ReviewStatus == ReviewStatus.Open),
            criticalFindings = ff.Count(f => f.Severity == FindingSeverity.Critical),
            marksCount = fm.Count,
            linksCount = links.Count(l => l.AssemblyFile == x.File.FileName),
            storage = x.File.StoragePath
        };
    }));
});

app.MapGet("/api/findings", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.Findings
        .OrderByDescending(f => f.Severity)
        .ThenByDescending(f => f.CreatedAt)
        .ToListAsync());
});

app.MapGet("/api/marks/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.CheckMarks.Where(c => c.FileName == file).OrderBy(c => c.CreatedAt).ToListAsync());
});

app.MapPost("/api/marks", async (CheckMark m) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    m.Id = Guid.NewGuid();
    m.CreatedAt = DateTime.UtcNow;
    db.CheckMarks.Add(m);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "mark", User = m.User ?? "web",
        Detail = $"{m.FileName}: marca {m.State} em ({m.X:0},{m.Y:0},{m.W:0}x{m.H:0})"
    });
    await db.SaveChangesAsync();
    return Results.Ok(m);
});

// Ingestão em lote de marca-texto detectado em scan -DC (HSV + componentes conexos)
app.MapPost("/api/marks/batch", async (BatchMarksReq req) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    if (req.ReplaceExisting)
    {
        var oldMarks = await db.CheckMarks.Where(c => c.FileName == req.FileName && c.User == "scan-dc").ToListAsync();
        var oldFindings = await db.Findings.Where(f => f.FileName == req.FileName && f.RuleId.StartsWith("SCAN-") && f.ReviewStatus == ReviewStatus.Open).ToListAsync();
        db.CheckMarks.RemoveRange(oldMarks);
        db.Findings.RemoveRange(oldFindings);
    }

    var imgW = Math.Max(1.0, req.ImageWidth);
    var imgH = Math.Max(1.0, req.ImageHeight);
    var createdMarks = new List<CheckMark>();
    var createdFindings = 0;
    var docCode = TitleBlockIdentifier.ExtractCodeFromFileName(req.FileName);

    foreach (var item in req.Marks)
    {
        var cm = new CheckMark
        {
            Id = Guid.NewGuid(),
            FileName = req.FileName,
            DocumentCode = docCode,
            X = item.X, Y = item.Y, W = item.W, H = item.H,
            State = item.State,
            Note = item.Note ?? (item.State == CheckState.CheckedOk ? "Marca amarela ingerida (-DC)" : "Marca de correção ingerida (-DC)"),
            User = "scan-dc",
            CreatedAt = DateTime.UtcNow
        };
        createdMarks.Add(cm);

        if (item.State is CheckState.NeedsCorrection or CheckState.NeedsRevalidation)
        {
            db.Findings.Add(new Finding
            {
                RuleId = item.State == CheckState.NeedsCorrection ? "SCAN-RED" : "SCAN-ORANGE",
                FileName = req.FileName,
                DocumentCode = docCode,
                Description = item.State == CheckState.NeedsCorrection
                    ? $"Marcação vermelha de correção detectada no scan (-DC) — verificar aplicação no desenho revisado"
                    : $"Marcação laranja detectada no scan (-DC) — região exige revalidação",
                Evidence = $"Região px ({item.X:0}, {item.Y:0}, {item.W:0}×{item.H:0})",
                Severity = item.State == CheckState.NeedsCorrection ? FindingSeverity.Warning : FindingSeverity.Info,
                Source = FindingSource.Rule,
                Kind = FindingKind.Objective,
                BBoxX = item.X / imgW, BBoxY = item.Y / imgH,
                BBoxW = item.W / imgW, BBoxH = item.H / imgH,
                CreatedBy = "scan-dc"
            });
            createdFindings++;
        }
    }

    db.CheckMarks.AddRange(createdMarks);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "dc-ingest", User = req.User ?? "web",
        Detail = $"{req.FileName}: {createdMarks.Count} marcas ingeridas ({createdMarks.Count(m => m.State == CheckState.CheckedOk)} amarelas, {createdFindings} pendências)"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { marks = createdMarks.Count, findings = createdFindings });
});

app.MapDelete("/api/marks/{id:guid}", async (Guid id) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var m = await db.CheckMarks.FindAsync(id);
    if (m == null) return Results.NotFound();
    db.CheckMarks.Remove(m);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = id });
});

app.MapDelete("/api/marks/file/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var list = await db.CheckMarks.Where(c => c.FileName == file).ToListAsync();
    db.CheckMarks.RemoveRange(list);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = list.Count });
});

app.MapPost("/api/release", async (ReleaseReq r) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var revs = await db.Revisions.Where(x => x.Code.ToUpper() == r.Code.ToUpper()).ToListAsync();
    var attempt = ReleaseControl.Evaluate(r.Code, r.Rev, r.Purpose, revs, r.User ?? "web");
    db.ReleaseAttempts.Add(attempt);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "release", User = r.User ?? "web",
        Detail = $"{r.Code} Rev.{r.Rev} ({r.Purpose}) → {attempt.Decision}: {attempt.Reason}"
    });
    await db.SaveChangesAsync();
    return Results.Ok(attempt);
});

app.MapGet("/api/releases", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.ReleaseAttempts.OrderByDescending(r => r.AttemptedAt).Take(100).ToListAsync());
});

// Gestão de Revisões e Autorização Controlada (CA-04 / CA-05 / RF-06)
app.MapGet("/api/revisions", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var revs = await db.Revisions.OrderBy(r => r.Code).ThenBy(r => r.Revision).ToListAsync();
    var files = await db.Files.ToListAsync();
    var findings = await db.Findings.Where(f => f.ReviewStatus != ReviewStatus.Dismissed && f.ReviewStatus != ReviewStatus.Corrected).ToListAsync();

    return Results.Ok(revs.Select(r =>
    {
        var sf = files.FirstOrDefault(f => f.Id == r.SourceFileId)
                 ?? files.FirstOrDefault(f => string.Equals(f.Identity.Code, r.Code, StringComparison.OrdinalIgnoreCase));
        var docFindings = findings.Where(f =>
            string.Equals(f.DocumentCode, r.Code, StringComparison.OrdinalIgnoreCase) ||
            (sf != null && f.FileName == sf.FileName)).ToList();
        var blockers = docFindings.Count(f => f.Severity is FindingSeverity.Critical or FindingSeverity.Error);
        return new
        {
            r.Id, r.Code, r.Revision, r.ReceivedAt,
            state = r.State.ToString(),
            stateValue = (int)r.State,
            r.AuthorizedForFabrication,
            r.AuthorizedForAssembly,
            r.AuthorizedForConsultation,
            fileName = sf?.FileName,
            openFindings = docFindings.Count,
            blockingFindings = blockers
        };
    }));
});

app.MapPost("/api/revisions/sync", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var files = await db.Files.ToListAsync();
    var revs = await db.Revisions.ToListAsync();
    var created = 0;
    foreach (var f in files.Where(x => !TitleBlockIdentifier.IsCheckedScan(x.FileName)))
    {
        var code = f.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName);
        if (string.IsNullOrWhiteSpace(code)) continue;
        var rev = f.Identity.Revision ?? TitleBlockIdentifier.ExtractRevisionFromFileName(f.FileName) ?? "—";
        if (revs.Any(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(r.Revision, rev, StringComparison.OrdinalIgnoreCase)))
            continue;
        var nr = new DocumentRevision { BatchId = f.BatchId, Code = code.ToUpperInvariant(), Revision = rev, SourceFileId = f.Id };
        db.Revisions.Add(nr);
        revs.Add(nr);
        created++;
    }
    if (created > 0) await db.SaveChangesAsync();
    return Results.Ok(new { created, total = revs.Count });
});

app.MapPost("/api/revisions/{id:guid}/state", async (Guid id, RevisionStateReq req) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var r = await db.Revisions.FindAsync(id);
    if (r == null) return Results.NotFound();
    r.State = req.State;
    if (req.State == CheckState.NeedsCorrection)
    {
        // CA-05: revisão marcada com erro não pode permanecer autorizada para fabricação/montagem
        r.AuthorizedForFabrication = false;
        r.AuthorizedForAssembly = false;
    }
    db.AuditLog.Add(new AuditEvent
    {
        Action = "revision-state", User = req.User ?? "web",
        Detail = $"{r.Code} Rev.{r.Revision} → estado {r.State}"
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
});

app.MapPost("/api/revisions/{id:guid}/authorize", async (Guid id, AuthorizeReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.User))
        return Results.BadRequest("Identificação do responsável pela autorização é obrigatória.");

    await using var db = new CheckDrawDbContext(dbPath);
    var r = await db.Revisions.FindAsync(id);
    if (r == null) return Results.NotFound();

    if (req.Grant)
    {
        // CA-05: vermelho não vira autorização sem revalidação
        if (r.State == CheckState.NeedsCorrection)
            return Results.BadRequest($"BLOQUEADO (CA-05): {r.Code} Rev.{r.Revision} está marcada com correção necessária.");

        var sf = await db.Files.FirstOrDefaultAsync(f => f.Id == r.SourceFileId);
        var blockers = await db.Findings.CountAsync(f =>
            (f.DocumentCode == r.Code || (sf != null && f.FileName == sf.FileName)) &&
            (f.Severity == FindingSeverity.Critical || f.Severity == FindingSeverity.Error) &&
            f.ReviewStatus != ReviewStatus.Dismissed &&
            f.ReviewStatus != ReviewStatus.Corrected);

        if (blockers > 0 && req.Purpose != ReleasePurpose.Consultation)
            return Results.BadRequest($"BLOQUEADO: existem {blockers} apontamento(s) Crítico/Erro não resolvidos em {r.Code}. Trate-os antes de autorizar para {req.Purpose}.");
    }

    switch (req.Purpose)
    {
        case ReleasePurpose.Fabrication: r.AuthorizedForFabrication = req.Grant; break;
        case ReleasePurpose.Assembly: r.AuthorizedForAssembly = req.Grant; break;
        default: r.AuthorizedForConsultation = req.Grant; break;
    }

    db.AuditLog.Add(new AuditEvent
    {
        Action = req.Grant ? "authorize" : "revoke-auth",
        User = req.User,
        Detail = $"{r.Code} Rev.{r.Revision}: {(req.Grant ? "autorizada" : "revogada")} para {req.Purpose}"
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
});

// Lista Mestra (ML-001 e ML-002)
app.MapGet("/api/master-list", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.MasterList.OrderBy(m => m.Code).ToListAsync());
});

app.MapPost("/api/master-list", async (MasterListEntry entry) =>
{
    if (string.IsNullOrWhiteSpace(entry.Code)) return Results.BadRequest("Código obrigatório");
    await using var db = new CheckDrawDbContext(dbPath);
    entry.Id = Guid.NewGuid();
    entry.Code = entry.Code.Trim().ToUpperInvariant();
    entry.ExpectedRevision = (entry.ExpectedRevision ?? "").Trim().ToUpperInvariant();
    db.MasterList.Add(entry);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "master-list-add", User = "web",
        Detail = $"Lista mestra: {entry.Code} Rev.{entry.ExpectedRevision} ({entry.Title})"
    });
    await db.SaveChangesAsync();
    return Results.Ok(entry);
});

app.MapDelete("/api/master-list/{id:guid}", async (Guid id) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var e = await db.MasterList.FindAsync(id);
    if (e == null) return Results.NotFound();
    db.MasterList.Remove(e);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = id });
});

app.MapPost("/api/master-list/validate", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    if (batch == null) return Results.BadRequest("Nenhum lote importado");
    var master = await db.MasterList.ToListAsync();

    var stale = await db.Findings.Where(f => (f.RuleId == "ML-001" || f.RuleId == "ML-002") && f.ReviewStatus == ReviewStatus.Open).ToListAsync();
    db.Findings.RemoveRange(stale);

    // Para PDFs cujo código foi extraído sem carimbo CAD atributado, promove temporariamente no contexto
    var shadowBatch = new Batch { Id = batch.Id, Supplier = batch.Supplier, PackageName = batch.PackageName };
    foreach (var f in batch.Files.Where(x => !TitleBlockIdentifier.IsCheckedScan(x.FileName)))
    {
        var code = f.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName);
        var rev = f.Identity.Revision ?? TitleBlockIdentifier.ExtractRevisionFromFileName(f.FileName) ?? "";
        shadowBatch.Files.Add(new DrawingFile
        {
            Id = f.Id, FileName = f.FileName, Extension = f.Extension, Health = f.Health,
            Identity = new DocumentIdentity
            {
                Code = string.IsNullOrEmpty(code) ? null : code,
                Revision = rev,
                Status = string.IsNullOrEmpty(code) ? IdentificationStatus.Unidentified : IdentificationStatus.Identified
            }
        });
    }

    var ctx = new RuleContext { Batch = shadowBatch, Contract = new ContractProfile(), MasterList = master };
    var generated = new List<Finding>();
    generated.AddRange(new MasterListMissingRule().Evaluate(ctx));
    generated.AddRange(new MasterListRevisionMismatchRule().Evaluate(ctx));
    foreach (var g in generated) g.CreatedBy = "CheckDraw/master-list";

    db.Findings.AddRange(generated);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "master-list-validate", User = "web",
        Detail = $"Lista mestra ({master.Count} docs) validada contra lote: {generated.Count} apontamento(s)"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { masterEntries = master.Count, findings = generated.Count });
});

app.MapPost("/api/transfer", async (TransferReq r) =>
{
    var svc = new RevisionTransferService(extractors);
    var res = await svc.TransferAsync(dbPath, r.Code, r.From, r.To);
    return res.Error != null ? Results.BadRequest(res.Error) : Results.Ok(res);
});

// Análise de desenho por IA: imagem/PDF → findings com bbox + correção sugerida
app.MapPost("/api/analyze", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest("imagem obrigatória");
    var fileName = form["fileName"].FirstOrDefault() ?? file.FileName;

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);

    await using var db = new CheckDrawDbContext(dbPath);
    var (count, carimbo, err) = await AnalyzeAndPersistAsync(db, ms.ToArray(), fileName);
    if (err != null) return Results.BadRequest(err);
    return Results.Ok(new { fileName, carimbo, findings = count });
});

// Análise IA direta pelo arquivo já armazenado (PDF ou imagem no Storage/disco)
app.MapPost("/api/analyze-file/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var ef = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == file);
    var bytes = await LoadFileBytesAsync(ef);
    if (bytes == null) return Results.BadRequest("Arquivo sem PDF/imagem acessível no Storage (para DWG, abra no Viewer ou exporte PDF)");

    var (count, carimbo, err) = await AnalyzeAndPersistAsync(db, bytes, file);
    if (err != null) return Results.BadRequest(err);
    return Results.Ok(new { fileName = file, carimbo, findings = count });
});

// Análise IA em lote para todos os PDFs/imagens do último lote (Job assíncrono)
app.MapPost("/api/analyze-all", () =>
{
    var jobId = jobs.Enqueue(async state =>
    {
        var ai = new GeminiAnalyzer();
        if (!ai.Available) throw new InvalidOperationException("GEMINI_API_KEY não configurada");

        await using var db = new CheckDrawDbContext(dbPath);
        var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
        if (batch == null) throw new InvalidOperationException("Nenhum lote importado");

        var targets = batch.Files
            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                        !TitleBlockIdentifier.IsCheckedScan(f.FileName))
            .ToList();

        var totalFindings = 0;
        var done = 0;
        foreach (var f in targets)
        {
            done++;
            state.Progress = $"Analisando IA ({done}/{targets.Count}): {f.FileName}";
            var bytes = await LoadFileBytesAsync(f);
            if (bytes == null) continue;
            await using var dbStep = new CheckDrawDbContext(dbPath);
            var (cnt, _, _) = await AnalyzeAndPersistAsync(dbStep, bytes, f.FileName);
            totalFindings += cnt;
        }
        state.Progress = "Concluído";
        state.Result = new { analyzed = done, findings = totalFindings };
    });
    return Results.Ok(new { jobId, status = "queued" });
});

// Limpeza: remove achados de IA ainda não revisados (preserva vereditos humanos)
app.MapPost("/api/findings/purge-open-ai", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var open = await db.Findings
        .Where(f => f.Source == FindingSource.Ai && f.ReviewStatus == ReviewStatus.Open)
        .ToListAsync();
    db.Findings.RemoveRange(open);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = open.Count });
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

// Cruzamento conjunto→detalhe: lê quadro de aplicação do conjunto vetorial ou via IA,
// casa os códigos de detalhe com os arquivos importados e grava DrawingLinks sem duplicar
app.MapPost("/api/crosscheck/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == file);
    if (f == null || f.StoragePath?.StartsWith("aps:") == true)
        return Results.BadRequest("arquivo sem PDF acessível para leitura de quadro");

    var bytes = await LoadFileBytesAsync(f);
    if (bytes == null) return Results.BadRequest("arquivo sem storage acessível");

    var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
    await File.WriteAllBytesAsync(tmp, bytes);
    var ext = new VectorPdfExtractor().Extract(tmp);
    File.Delete(tmp);

    var links = ext.Success
        ? new CheckDraw.Infrastructure.CrossCheck.CrossCheckService().DiscoverLinks(ext, file)
        : new List<DrawingLink>();

    var via = "vector";
    if (links.Count == 0)
    {
        var ai = new GeminiAnalyzer();
        if (ai.Available)
        {
            var aiLinks = await ai.ReadApplicationTableAsync(bytes, "application/pdf");
            links = aiLinks
                .Where(l => !string.IsNullOrWhiteSpace(l.detail))
                .Select(l => new DrawingLink
                {
                    AssemblyFile = file, Marca = l.marca, DetailCode = l.detail.Trim().ToUpperInvariant(),
                    Qty = l.qty, UnitWeight = l.weight
                }).ToList();
            via = "ai";
        }
    }

    // Deduplica por (Marca, DetailCode) e remove vínculos anteriores deste conjunto
    links = links
        .GroupBy(l => (l.Marca.Trim().ToUpperInvariant(), l.DetailCode.Trim().ToUpperInvariant()))
        .Select(g => g.First())
        .ToList();

    var prevLinks = await db.DrawingLinks.Where(l => l.AssemblyFile == file).ToListAsync();
    db.DrawingLinks.RemoveRange(prevLinks);

    var prevMiss = await db.Findings
        .Where(x => x.FileName == file && x.RuleId == "XCHK-MISS" && x.ReviewStatus == ReviewStatus.Open)
        .ToListAsync();
    db.Findings.RemoveRange(prevMiss);

    var allFiles = await db.Files.ToListAsync();
    var deliverables = allFiles.Where(x => !TitleBlockIdentifier.IsCheckedScan(x.FileName)).ToList();

    foreach (var l in links)
    {
        var det = deliverables.FirstOrDefault(x =>
            x.FileName != file &&
            (x.FileName.Contains(l.DetailCode, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(x.Identity.Code, l.DetailCode, StringComparison.OrdinalIgnoreCase)));
        l.DetailFileName = det?.FileName;
        l.Status = det == null ? "missing" : "proposed";

        if (det == null)
        {
            db.Findings.Add(new Finding
            {
                RuleId = "XCHK-MISS",
                FileName = file,
                DocumentCode = l.DetailCode,
                Description = $"Detalhe '{l.DetailCode}' (marca {l.Marca}, qtd {l.Qty?.ToString() ?? "n/d"}) citado no Quadro de Aplicação não consta nos arquivos importados",
                Evidence = $"Quadro de aplicação de {file} ({via})",
                Severity = FindingSeverity.Error,
                Source = via == "ai" ? FindingSource.Ai : FindingSource.Rule,
                Kind = via == "ai" ? FindingKind.Suspicion : FindingKind.Objective,
                CreatedBy = "crosscheck"
            });
        }
    }

    db.DrawingLinks.AddRange(links);
    db.AuditLog.Add(new AuditEvent
    {
        Action = "crosscheck", User = "system",
        Detail = $"{file} ({via}): {links.Count} vínculos, {links.Count(l => l.Status == "missing")} ausentes"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        file, vectors = ext.Geometry.Count, texts = ext.Texts.Count, via,
        links = links.Count, missing = links.Count(l => l.Status == "missing"),
        items = links
    });
});

static async Task<(bool itemFound, int count, int mismatches)> RunPairComparisonAsync(
    CheckDrawDbContext db, GeminiAnalyzer ai,
    string assembly, byte[] assemblyBytes,
    string detail, byte[] detailBytes,
    DrawingLink? link)
{
    var marca = link?.Marca ?? "item";
    var ruleId = $"XCHK-{marca}";
    var pairName = $"{assembly} ↔ {detail}";

    // Remove comparações abertas anteriores do mesmo par+marca
    var prev = await db.Findings
        .Where(f => f.FileName == pairName && f.RuleId == ruleId && f.ReviewStatus == ReviewStatus.Open)
        .ToListAsync();
    if (prev.Count > 0) db.Findings.RemoveRange(prev);

    var cmp = await ai.CompareDrawingsAsync(assemblyBytes, detailBytes, marca, link?.Qty, link?.UnitWeight, link?.DetailCode);
    if (cmp == null) return (false, 0, 0);

    var itemFound = !cmp.Value.TryGetProperty("item_found", out var ifProp) ||
                    ifProp.ValueKind != JsonValueKind.False;

    var count = 0;
    var mismatches = 0;

    if (!itemFound)
    {
        db.Findings.Add(new Finding
        {
            RuleId = ruleId,
            FileName = pairName,
            DocumentCode = link?.DetailCode,
            Description = $"Desenho de detalhe '{detail}' não corresponde ao código/item declarado ('{link?.DetailCode ?? marca}') no Quadro de Aplicação do conjunto",
            Evidence = $"Conjunto {assembly} vs Detalhe {detail}",
            Severity = FindingSeverity.Critical,
            Source = FindingSource.Ai, Kind = FindingKind.Suspicion, Confidence = 0.85,
            CreatedBy = "gemini"
        });
        count++;
        mismatches++;
    }

    if (cmp.Value.TryGetProperty("comparisons", out var arr))
    {
        foreach (var c in arr.EnumerateArray())
        {
            var res = c.TryGetProperty("result", out var rp) ? (rp.GetString() ?? "insufficient") : "insufficient";
            if (res == "mismatch") mismatches++;
            var ch = c.TryGetProperty("characteristic", out var cp) ? cp.GetString() : "característica";
            var av = c.TryGetProperty("assembly", out var ap) ? ap.GetString() : "—";
            var dv = c.TryGetProperty("detail", out var dp) ? dp.GetString() : "—";
            var ev = c.TryGetProperty("evidence", out var ep) ? ep.GetString() ?? "" : "";

            db.Findings.Add(new Finding
            {
                RuleId = ruleId,
                FileName = pairName,
                DocumentCode = link?.DetailCode,
                Description = $"{ch}: conjunto '{av}' vs detalhe '{dv}'",
                Evidence = ev,
                Severity = res == "mismatch" ? FindingSeverity.Critical
                         : res == "insufficient" ? FindingSeverity.Warning : FindingSeverity.Info,
                Source = FindingSource.Ai, Kind = FindingKind.Suspicion,
                Confidence = res == "match" ? 0.9 : 0.75,
                CreatedBy = "gemini"
            });
            count++;
        }
    }

    if (link != null)
        link.Status = mismatches > 0 ? "divergent" : "confirmed";

    return (itemFound, count, mismatches);
}

// RF-02..05: comparação conjunto×detalhe por link — achados com evidência nas 2 folhas
app.MapPost("/api/crosscompare/{assembly}/{detail}", async (string assembly, string detail) =>
{
    var ai = new GeminiAnalyzer();
    if (!ai.Available) return Results.BadRequest("GEMINI_API_KEY não configurada");
    await using var db = new CheckDrawDbContext(dbPath);

    var fa = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == assembly);
    var fd = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == detail);
    var a = await LoadFileBytesAsync(fa);
    var d = await LoadFileBytesAsync(fd);
    if (a == null || d == null) return Results.BadRequest("arquivo sem PDF acessível no Storage");

    var detailCode = TitleBlockIdentifier.ExtractCodeFromFileName(detail);
    var link = await db.DrawingLinks.FirstOrDefaultAsync(l =>
        l.AssemblyFile == assembly && (l.DetailFileName == detail || l.DetailCode == detailCode));

    var (itemFound, count, mismatches) = await RunPairComparisonAsync(db, ai, assembly, a, detail, d, link);
    if (count == 0 && ai.LastError != null) return Results.BadRequest(ai.LastError);

    db.AuditLog.Add(new AuditEvent
    {
        Action = "crosscompare", User = "gemini",
        Detail = $"{assembly}↔{detail} ({link?.Marca ?? "item"}): {count} comparações, {mismatches} divergências"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { assembly, detail, marca = link?.Marca ?? "item", itemFound, comparisons = count, mismatches });
});

// Cruzamento em lote: todos os detalhes vinculados do conjunto, em background
app.MapPost("/api/crosscompare-all/{assembly}", (string assembly) =>
{
    var jobId = jobs.Enqueue(async state =>
    {
        var ai = new GeminiAnalyzer();
        if (!ai.Available) throw new InvalidOperationException("GEMINI_API_KEY ausente");
        await using var db = new CheckDrawDbContext(dbPath);
        var fa = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == assembly);
        var a = await LoadFileBytesAsync(fa);
        if (a == null) throw new InvalidOperationException("conjunto sem storage acessível");

        var links = await db.DrawingLinks
            .Where(l => l.AssemblyFile == assembly && l.DetailFileName != null).ToListAsync();

        var done = 0;
        var totalMismatches = 0;
        foreach (var l in links)
        {
            done++;
            state.Progress = $"Comparando par ({done}/{links.Count}): marca {l.Marca} → {l.DetailCode}";
            var fd = await db.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == l.DetailFileName);
            var d = await LoadFileBytesAsync(fd);
            if (d == null) continue;
            var (_, _, mm) = await RunPairComparisonAsync(db, ai, assembly, a, fd!.FileName, d, l);
            totalMismatches += mm;
        }
        db.AuditLog.Add(new AuditEvent
        {
            Action = "crosscompare-all", User = "gemini",
            Detail = $"{assembly}: {done} pares comparados de {links.Count} vinculados ({totalMismatches} divergências)"
        });
        await db.SaveChangesAsync();
        state.Progress = "Concluído";
        state.Result = new { pairs = done, mismatches = totalMismatches };
    });
    return Results.Ok(new { jobId, assembly, status = "queued" });
});

app.MapGet("/api/links", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.DrawingLinks.OrderBy(l => l.AssemblyFile).ThenBy(l => l.Marca).ToListAsync());
});

app.MapGet("/api/links/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.DrawingLinks
        .Where(l => l.AssemblyFile == file)
        .OrderBy(l => l.Marca).ToListAsync());
});

app.MapPost("/api/links", async (DrawingLink link) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    link.Id = Guid.NewGuid();
    link.DetailCode = link.DetailCode.Trim().ToUpperInvariant();
    if (string.IsNullOrEmpty(link.DetailFileName))
    {
        var det = await db.Files.FirstOrDefaultAsync(x =>
            x.FileName.Contains(link.DetailCode) && !x.FileName.Contains("-DC"));
        link.DetailFileName = det?.FileName;
        link.Status = det == null ? "missing" : "proposed";
    }
    db.DrawingLinks.Add(link);
    await db.SaveChangesAsync();
    return Results.Ok(link);
});

app.MapDelete("/api/links/{id:guid}", async (Guid id) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var l = await db.DrawingLinks.FindAsync(id);
    if (l == null) return Results.NotFound();
    db.DrawingLinks.Remove(l);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = id });
});

// Token somente-leitura para renderizar DWGs traduzidos (SVF2) no Autodesk APS Viewer
app.MapGet("/api/aps/token", async () =>
{
    var aps = new CheckDraw.Infrastructure.Aps.ApsService();
    if (!aps.Available) return Results.BadRequest("APS_CLIENT_ID/SECRET não configurados");
    var (token, expiresIn) = await aps.GetViewerTokenAsync();
    return Results.Ok(new { access_token = token, expires_in = expiresIn });
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

    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    if (batch == null)
    {
        var cp = await db.Contracts.FirstOrDefaultAsync() ?? new ContractProfile { Name = "Padrão" };
        if (cp.Id == Guid.Empty || !db.Contracts.Local.Any()) db.Contracts.Add(cp);
        batch = new Batch { Supplier = "upload", Contract = cp };
        db.Batches.Add(batch);
    }
    var code = TitleBlockIdentifier.ExtractCodeFromFileName(name);
    var drawingFile = new DrawingFile
    {
        FileName = name, FullPath = $"aps-oss:{name}", Extension = Path.GetExtension(name),
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        SizeBytes = bytes.Length, Health = FileHealth.Ok,
        Identity = new DocumentIdentity { Code = string.IsNullOrEmpty(code) ? null : code }
    };
    batch.Files.Add(drawingFile);
    await db.SaveChangesAsync();

    var jobId = jobs.Enqueue(async state =>
    {
        state.Progress = $"Enviando {name} ao Autodesk OSS...";
        var urn = await aps.UploadAsync("checkdraw_dwg", $"{Guid.NewGuid():N}_{name}", bytes);
        state.Progress = $"Traduzindo {name} para SVF2 na nuvem Autodesk...";
        await aps.TranslateAndWaitAsync(urn);
        await using var db2 = new CheckDrawDbContext(dbPath);
        var f = await db2.Files.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.FileName == name);
        if (f != null) { f.StoragePath = $"aps:{urn}"; await db2.SaveChangesAsync(); }
        state.Progress = "Tradução DWG concluída";
        state.Result = new { name, urn };
    });
    return Results.Ok(new { jobId, name, status = "queued" });
});

// Lista todos os arquivos do banco (para reanálise em lote e relatórios)
app.MapGet("/api/files", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var fs = await db.Files.OrderBy(x => x.FileName)
        .Select(x => new
        {
            x.FileName, x.StoragePath, x.Extension, x.Health,
            code = x.Identity.Code, rev = x.Identity.Revision,
            isCheckedScan = TitleBlockIdentifier.IsCheckedScan(x.FileName)
        }).ToListAsync();
    return Results.Ok(fs);
});

// Lookup: storage path por nome de arquivo (viewer encontra PDFs/DWGs de qualquer lote)
app.MapGet("/api/files/{name}", async (string name) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Files.Where(x => x.FileName == name)
        .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
    return f == null ? Results.NotFound() : Results.Ok(new
    {
        f.FileName, f.StoragePath, f.Extension,
        code = f.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName),
        rev = f.Identity.Revision,
        isCheckedScan = TitleBlockIdentifier.IsCheckedScan(f.FileName)
    });
});

// Veredito do revisor por apontamento → status + review + feedback pair (RAG-lite)
app.MapPost("/api/findings/{id:guid}/review", async (Guid id, ReviewReq r) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var f = await db.Findings.FindAsync(id);
    if (f == null) return Results.NotFound();
    f.ReviewStatus = Enum.Parse<ReviewStatus>(r.Action, true);
    var reviewer = string.IsNullOrWhiteSpace(r.Reviewer) ? "web" : r.Reviewer.Trim();

    db.FindingReviews.Add(new FindingReview
    {
        FindingId = id, Action = r.Action.ToLowerInvariant(),
        Comment = r.Comment, CorrectedValue = r.CorrectedValue, Reviewer = reviewer
    });
    db.AiFeedbackPairs.Add(new AiFeedbackPair
    {
        FileName = f.FileName, ChecklistCode = f.RuleId,
        AiOutput = f.Description + (f.Evidence != null ? $" ({f.Evidence})" : ""),
        HumanAction = r.Action.ToLowerInvariant(),
        HumanCorrection = r.CorrectedValue,
        WasCorrect = r.Action.Equals("confirmed", StringComparison.OrdinalIgnoreCase),
        Reviewer = reviewer
    });
    db.AuditLog.Add(new AuditEvent
    {
        Action = "review",
        Detail = $"{f.RuleId} em {f.FileName}: {r.Action}" + (r.CorrectedValue != null ? $" → '{r.CorrectedValue}'" : ""),
        User = reviewer
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { f.ReviewStatus });
});

// Score de conformidade ponderado por arquivo
app.MapGet("/api/score/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var fs = await db.Findings.Where(f => f.FileName == file || (f.FileName != null && f.FileName.StartsWith(file + " ↔"))).ToListAsync();
    var open = fs.Where(f => f.ReviewStatus != ReviewStatus.Dismissed && f.ReviewStatus != ReviewStatus.Corrected).ToList();
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

// Checklist: CRUD dos critérios + seed padrão + status por arquivo
app.MapGet("/api/checklist", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.ChecklistItems.OrderBy(c => c.Code).ToListAsync());
});

app.MapPost("/api/checklist", async (ChecklistItem item) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    item.Id = Guid.NewGuid();
    item.Code = item.Code.Trim().ToUpperInvariant();
    db.ChecklistItems.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(item.Id);
});

app.MapPost("/api/checklist/seed-defaults", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var added = EnsureDefaultChecklist(db, onlyIfEmpty: false);
    return Results.Ok(new { added });
});

app.MapDelete("/api/checklist/{id:guid}", async (Guid id) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var c = await db.ChecklistItems.FindAsync(id);
    if (c == null) return Results.NotFound();
    db.ChecklistItems.Remove(c);
    await db.SaveChangesAsync();
    return Results.Ok(new { removed = id });
});

app.MapGet("/api/checklist/{file}", async (string file) =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var items = await db.ChecklistItems.ToListAsync();
    var findings = await db.Findings.Where(f => f.FileName == file).ToListAsync();
    return Results.Ok(items.Select(i => new
    {
        i.Code, i.Category, i.Description, severity = i.Severity.ToString(),
        status = findings.Any(f => f.RuleId == i.Code && f.ReviewStatus != ReviewStatus.Dismissed) ? "finding" : "ok",
        findings = findings.Where(f => f.RuleId == i.Code).Select(f => f.Description)
    }));
});

// Relatório Técnico Consolidado & Trilha de Auditoria
app.MapGet("/api/report", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    var batch = await db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefaultAsync();
    var files = batch?.Files ?? await db.Files.ToListAsync();
    var findings = await db.Findings.OrderByDescending(f => f.Severity).ToListAsync();
    var revisions = await db.Revisions.OrderBy(r => r.Code).ThenBy(r => r.Revision).ToListAsync();
    var links = await db.DrawingLinks.OrderBy(l => l.AssemblyFile).ThenBy(l => l.Marca).ToListAsync();
    var marks = await db.CheckMarks.ToListAsync();
    var releases = await db.ReleaseAttempts.OrderByDescending(r => r.AttemptedAt).Take(50).ToListAsync();
    var audit = await db.AuditLog.OrderByDescending(a => a.At).Take(100).ToListAsync();

    var fileRows = files.OrderBy(f => f.FileName).Select(f =>
    {
        var ff = findings.Where(x => x.FileName == f.FileName && x.ReviewStatus != ReviewStatus.Dismissed).ToList();
        var penalty = ff.Sum(x => x.Severity == FindingSeverity.Critical ? 15
            : x.Severity == FindingSeverity.Error ? 10
            : x.Severity == FindingSeverity.Warning ? 5 : 2);
        var fm = marks.Where(m => m.FileName == f.FileName).ToList();
        return new
        {
            f.FileName,
            code = f.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName),
            rev = f.Identity.Revision ?? "—",
            health = f.Health.ToString(),
            status = f.Identity.Status.ToString(),
            isCheckedScan = TitleBlockIdentifier.IsCheckedScan(f.FileName),
            conformityScore = Math.Max(0, 100 - penalty),
            openFindings = ff.Count(x => x.ReviewStatus == ReviewStatus.Open),
            confirmedFindings = ff.Count(x => x.ReviewStatus == ReviewStatus.Confirmed),
            criticalFindings = ff.Count(x => x.Severity == FindingSeverity.Critical),
            yellowMarks = fm.Count(m => m.State == CheckState.CheckedOk),
            redMarks = fm.Count(m => m.State == CheckState.NeedsCorrection)
        };
    }).ToList();

    return Results.Ok(new
    {
        generatedAt = DateTime.UtcNow,
        packageName = batch?.PackageName ?? "Repositório Geral",
        supplier = batch?.Supplier ?? "",
        summary = new
        {
            totalFiles = files.Count,
            deliverableFiles = files.Count(f => !TitleBlockIdentifier.IsCheckedScan(f.FileName)),
            checkedScans = files.Count(f => TitleBlockIdentifier.IsCheckedScan(f.FileName)),
            avgConformity = fileRows.Count > 0 ? Math.Round(fileRows.Average(x => x.conformityScore), 1) : 100,
            totalFindings = findings.Count,
            openFindings = findings.Count(f => f.ReviewStatus == ReviewStatus.Open),
            confirmedFindings = findings.Count(f => f.ReviewStatus == ReviewStatus.Confirmed),
            correctedFindings = findings.Count(f => f.ReviewStatus == ReviewStatus.Corrected),
            dismissedFindings = findings.Count(f => f.ReviewStatus == ReviewStatus.Dismissed),
            criticalActive = findings.Count(f => f.Severity == FindingSeverity.Critical && f.ReviewStatus != ReviewStatus.Dismissed && f.ReviewStatus != ReviewStatus.Corrected),
            totalLinks = links.Count,
            missingLinks = links.Count(l => l.Status == "missing"),
            divergentLinks = links.Count(l => l.Status == "divergent"),
            authorizedRevisions = revisions.Count(r => r.AuthorizedForFabrication || r.AuthorizedForAssembly)
        },
        files = fileRows,
        revisions,
        links,
        findings = findings.Where(f => f.ReviewStatus != ReviewStatus.Dismissed).Take(300),
        releases,
        audit
    });
});

app.MapGet("/api/audit", async () =>
{
    await using var db = new CheckDrawDbContext(dbPath);
    return Results.Ok(await db.AuditLog.OrderByDescending(a => a.At).Take(200).ToListAsync());
});

app.MapGet("/api/health", () =>
{
    var ai = new GeminiAnalyzer();
    var aps = new CheckDraw.Infrastructure.Aps.ApsService();
    return Results.Ok(new
    {
        status = "ok",
        db = dbPath.Contains("Host=") ? "postgres" : "sqlite",
        storage = storageOk,
        ai = ai.Available,
        aps = aps.Available
    });
});

app.Run();

record ReviewReq(string Action, string? Comment, string? CorrectedValue, string? Reviewer);
record InstantiateReq(Guid TemplateId, Guid ContractProfileId);
record ImportReq(string Dir, string? Supplier);
record ReleaseReq(string Code, string Rev, ReleasePurpose Purpose, string? User);
record TransferReq(string Code, string From, string To);
record RevisionStateReq(CheckState State, string? User);
record AuthorizeReq(ReleasePurpose Purpose, bool Grant, string? User);
record MarkRegionItem(double X, double Y, double W, double H, CheckState State, string? Note);
record BatchMarksReq(string FileName, double ImageWidth, double ImageHeight, bool ReplaceExisting, string? User, List<MarkRegionItem> Marks);
