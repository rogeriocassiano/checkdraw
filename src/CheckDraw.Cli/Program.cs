using System.Text.Json;
using CheckDraw.Cli;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Release;
using CheckDraw.Extractors.Dxf;
using CheckDraw.Infrastructure.Data;
using CheckDraw.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

var args0 = args.Length > 0 ? args[0] : "help";

switch (args0)
{
    case "corpus":
        CorpusGenerator.Generate(Arg(args, 1, "corpus"));
        break;

    case "import":
        await Import(Arg(args, 1, "corpus"), Opt(args, "--db", "checkdraw.db"),
            Opt(args, "--supplier", ""), Opt(args, "--package", ""));
        break;

    case "report":
        await Report(Opt(args, "--db", "checkdraw.db"));
        break;

    case "check":
        await SetCheckState(Opt(args, "--db", "checkdraw.db"),
            Opt(args, "--code", ""), Opt(args, "--rev", ""), Opt(args, "--state", ""));
        break;

    case "authorize":
        await Authorize(Opt(args, "--db", "checkdraw.db"),
            Opt(args, "--code", ""), Opt(args, "--rev", ""), Opt(args, "--purpose", ""));
        break;

    case "release":
        await Release(Opt(args, "--db", "checkdraw.db"),
            Opt(args, "--code", ""), Opt(args, "--rev", ""), Opt(args, "--purpose", ""));
        break;

    default:
        Console.WriteLine("""
            CheckDraw CLI — prova de conceito
              corpus <dir>                          gera corpus sintético de teste
              import <dir> --db <file>              importa lote, extrai, aplica regras
              report --db <file>                    índice do pacote + fila de conferência + pendências
              check --db <f> --code C --rev R --state ok|correction|revalidation
              authorize --db <f> --code C --rev R --purpose fabrication|assembly|consultation
              release --db <f> --code C --rev R --purpose ...   avalia emissão (auditado)
            """);
        break;
}

static string Arg(string[] a, int i, string def) => a.Length > i ? a[i] : def;
static string Opt(string[] a, string name, string def) =>
    Array.IndexOf(a, name) is int i && i >= 0 && a.Length > i + 1 ? a[i + 1] : def;

static async Task Import(string dir, string dbPath, string supplier, string package)
{
    var contractPath = Path.Combine(dir, "contrato.json");
    var masterPath = Path.Combine(dir, "lista-mestra.json");

    var contract = File.Exists(contractPath)
        ? JsonSerializer.Deserialize<ContractProfile>(await File.ReadAllTextAsync(contractPath))!
        : new ContractProfile { Name = "Contrato padrão" };
    var master = File.Exists(masterPath)
        ? JsonSerializer.Deserialize<List<MasterListEntry>>(await File.ReadAllTextAsync(masterPath))!
        : new List<MasterListEntry>();

    var importer = new BatchImporter(new[] { new DxfExtractor() });
    var result = await importer.ImportAsync(dir, contract, master, dbPath, supplier, package);

    var b = result.Batch;
    Console.WriteLine($"Lote '{b.PackageName}' — {b.Files.Count} arquivos recebidos\n");
    Console.WriteLine($"{"ARQUIVO",-22} {"SAÚDE",-10} {"CÓDIGO",-10} {"REV",-4} {"IDENTIFICAÇÃO"}");
    foreach (var f in b.Files.OrderBy(f => f.FileName))
        Console.WriteLine($"{f.FileName,-22} {f.Health,-10} {f.Identity.Code ?? "—",-10} {f.Identity.Revision ?? "—",-4} {f.Identity.Status}");

    Console.WriteLine($"\n{"SCORE",-6} FILA DE CONFERÊNCIA (maior risco primeiro)");
    foreach (var (f, s) in result.ReviewQueue.Take(10))
        Console.WriteLine($"{s,-6} {f.FileName}  [{f.Identity.Code} Rev. {f.Identity.Revision}]");
    if (result.ReviewQueue.Count > 10) Console.WriteLine($"       … e mais {result.ReviewQueue.Count - 10}");

    PrintFindings(result.Findings);
    Console.WriteLine($"\n{result.Revisions.Count} revisões registradas — nenhuma autorizada por padrão (recebida ≠ liberada)");
    Console.WriteLine($"Banco: {dbPath}");
}

static async Task Report(string dbPath)
{
    await using var db = new CheckDrawDbContext(dbPath);
    var findings = await db.Findings.OrderByDescending(f => f.Severity).ToListAsync();
    var revisions = await db.Revisions.ToListAsync();
    var files = await db.Files.Include(f => f.Batch).ToListAsync();

    Console.WriteLine($"{"CÓDIGO",-10} {"REV",-4} {"ESTADO",-18} {"FAB",-4} {"MON",-4} {"CON"}");
    foreach (var r in revisions.OrderBy(r => r.Code).ThenBy(r => r.Revision))
        Console.WriteLine($"{r.Code,-10} {r.Revision,-4} {r.State,-18} {Mark(r.AuthorizedForFabrication),-4} {Mark(r.AuthorizedForAssembly),-4} {Mark(r.AuthorizedForConsultation)}");

    PrintFindings(findings);
    static string Mark(bool v) => v ? "✓" : "—";
}

static void PrintFindings(IReadOnlyList<Finding> findings)
{
    Console.WriteLine($"\n{"SEV",-9} {"REGRA",-8} {"DOC",-10} {"REV",-4} APONTAMENTO");
    foreach (var f in findings.OrderByDescending(f => f.Severity).ThenBy(f => f.FileName))
        Console.WriteLine($"{f.Severity,-9} {f.RuleId,-8} {f.DocumentCode ?? "—",-10} {f.Revision ?? "—",-4} {f.Description}");
    Console.WriteLine($"Total: {findings.Count} apontamentos");
}

static async Task SetCheckState(string dbPath, string code, string rev, string state)
{
    var parsed = state.ToLowerInvariant() switch
    {
        "ok" or "amarelo" => CheckState.CheckedOk,
        "correction" or "vermelho" => CheckState.NeedsCorrection,
        "revalidation" or "laranja" => CheckState.NeedsRevalidation,
        _ => (CheckState?)null
    };
    if (parsed == null) { Console.WriteLine("Estado inválido: ok|correction|revalidation"); return; }

    await using var db = new CheckDrawDbContext(dbPath);
    var r = await FindRevision(db, code, rev);
    if (r == null) { Console.WriteLine($"Revisão {rev} de '{code}' não encontrada"); return; }
    r.State = parsed.Value;
    db.AuditLog.Add(new AuditEvent { Action = "check", Detail = $"{code} Rev.{rev} → {parsed.Value}", User = "cli" });
    await db.SaveChangesAsync();
    Console.WriteLine($"{code} Rev.{rev}: {parsed.Value}");
}

static async Task Authorize(string dbPath, string code, string rev, string purpose)
{
    if (!Enum.TryParse<ReleasePurpose>(purpose, true, out var p)) { Console.WriteLine("Finalidade inválida"); return; }
    await using var db = new CheckDrawDbContext(dbPath);
    var r = await FindRevision(db, code, rev);
    if (r == null) { Console.WriteLine($"Revisão {rev} de '{code}' não encontrada"); return; }
    if (r.State == CheckState.NeedsCorrection)
    {
        Console.WriteLine($"BLOQUEADO: {code} Rev.{rev} tem correção pendente — a revalidação é necessária antes de autorizar");
        return;
    }
    switch (p)
    {
        case ReleasePurpose.Fabrication: r.AuthorizedForFabrication = true; break;
        case ReleasePurpose.Assembly: r.AuthorizedForAssembly = true; break;
        default: r.AuthorizedForConsultation = true; break;
    }
    db.AuditLog.Add(new AuditEvent { Action = "authorize", Detail = $"{code} Rev.{rev} autorizada p/ {p}", User = "cli" });
    await db.SaveChangesAsync();
    Console.WriteLine($"{code} Rev.{rev} autorizada para {p}");
}

static async Task Release(string dbPath, string code, string rev, string purpose)
{
    if (!Enum.TryParse<ReleasePurpose>(purpose, true, out var p)) { Console.WriteLine("Finalidade inválida"); return; }
    await using var db = new CheckDrawDbContext(dbPath);
    var revisions = await db.Revisions.Where(r => r.Code == code).ToListAsync();
    var attempt = ReleaseControl.Evaluate(code, rev, p, revisions, "cli");
    db.ReleaseAttempts.Add(attempt);
    db.AuditLog.Add(new AuditEvent { Action = "release", Detail = $"{attempt.Decision}: {attempt.Reason}", User = "cli" });
    await db.SaveChangesAsync();
    Console.WriteLine($"{attempt.Decision.ToString().ToUpperInvariant()}: {attempt.Reason}");
}

static Task<DocumentRevision?> FindRevision(CheckDrawDbContext db, string code, string rev) =>
    db.Revisions.FirstOrDefaultAsync(r => r.Code == code && r.Revision == rev);
