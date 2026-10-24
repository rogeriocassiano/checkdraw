using System.Security.Cryptography;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Engine.Identification;
using CheckDraw.Engine.Rules;
using CheckDraw.Engine.Triage;
using CheckDraw.Infrastructure.Data;

namespace CheckDraw.Infrastructure.Import;

/// <summary>
/// Pipeline de recebimento (RF-01): varre diretório, calcula hash, extrai via
/// extratores registrados, identifica pelo carimbo, registra revisões e aplica regras.
/// Um arquivo falho nunca interrompe o lote (CA-01).
/// </summary>
public class BatchImporter
{
    private static readonly IRule[] Rules =
    {
        new CorruptFileRule(), new DuplicateHashRule(), new UnidentifiedFileRule(),
        new MultipleRevisionsRule(), new MasterListMissingRule(),
        new MasterListRevisionMismatchRule(), new PdfRequiredRule(), new OverriddenDimensionRule()
    };

    private readonly List<IDocumentExtractor> _extractors;

    public BatchImporter(IEnumerable<IDocumentExtractor> extractors) =>
        _extractors = extractors.ToList();

    public async Task<ImportResult> ImportAsync(
        string directory, ContractProfile contract,
        IReadOnlyList<MasterListEntry> masterList,
        string dbPath, string supplier = "", string packageName = "")
    {
        var batch = new Batch
        {
            Supplier = supplier,
            PackageName = string.IsNullOrEmpty(packageName) ? Path.GetFileName(directory.TrimEnd('\\', '/')) : packageName,
            Contract = contract
        };
        var extractions = new Dictionary<Guid, ExtractionResult>();
        var findings = new List<Finding>();
        var revisions = new List<DocumentRevision>();
        var result = new ImportResult { Batch = batch };

        // 1. Hash + saúde do arquivo
        var files = Directory.EnumerateFiles(directory).Where(HasSupportedExtension).ToList();
        foreach (var path in files)
        {
            var file = new DrawingFile
            {
                FileName = Path.GetFileName(path),
                FullPath = path,
                Extension = Path.GetExtension(path).ToLowerInvariant(),
                SizeBytes = new FileInfo(path).Length
            };
            try { file.Sha256 = await HashAsync(path); }
            catch (Exception ex)
            {
                file.Health = FileHealth.Corrupt;
                file.HealthDetail = $"Falha ao ler: {ex.Message}";
            }
            batch.Files.Add(file);
        }

        // 2. Duplicados por conteúdo
        foreach (var g in batch.Files.Where(f => f.Sha256.Length > 0).GroupBy(f => f.Sha256).Where(g => g.Count() > 1))
            foreach (var f in g) f.Health = FileHealth.Duplicate;

        // 3. Extração (PDFs: só presença por ora; extração de campos vem com PdfPig)
        foreach (var file in batch.Files.Where(f => f.Health != FileHealth.Corrupt))
        {
            var extractor = _extractors.FirstOrDefault(e => e.CanRead(file.FullPath));
            if (extractor == null) continue;

            var extraction = extractor.Extract(file.FullPath);
            if (!extraction.Success)
            {
                file.Health = FileHealth.Corrupt;
                file.HealthDetail = extraction.Error;
                continue;
            }
            extractions[file.Id] = extraction;
            file.Identity = TitleBlockIdentifier.Identify(extraction, contract);
        }

        // 4. Registro de revisões (recebida ≠ autorizada — CA-04)
        foreach (var g in batch.Files
                     .Where(f => f.Identity.Status == IdentificationStatus.Identified && f.Identity.Code != null)
                     .GroupBy(f => (f.Identity.Code!, f.Identity.Revision ?? "")))
        {
            var source = g.First();
            revisions.Add(new DocumentRevision
            {
                BatchId = batch.Id,
                Code = g.Key.Item1,
                Revision = g.Key.Item2,
                SourceFileId = source.Id
            });
        }

        // 5. Regras determinísticas
        var ctx = new RuleContext
        {
            Batch = batch, Contract = contract,
            Extractions = extractions, MasterList = masterList
        };
        foreach (var rule in Rules)
            findings.AddRange(rule.Evaluate(ctx).Select(f => { f.CreatedBy = "CheckDraw/import"; return f; }));

        // 6. Persistência + auditoria
        await using var db = new CheckDrawDbContext(dbPath);
        await db.Database.EnsureCreatedAsync();
        db.Batches.Add(batch);
        db.Revisions.AddRange(revisions);
        db.MasterList.AddRange(masterList);
        db.Findings.AddRange(findings);
        db.AuditLog.Add(new AuditEvent
        {
            Action = "import",
            Detail = $"Lote '{batch.PackageName}' de '{supplier}': {batch.Files.Count} arquivos, " +
                     $"{findings.Count} apontamentos, {revisions.Count} revisões registradas"
        });
        await db.SaveChangesAsync();

        result.Extractions = extractions;
        result.Findings = findings;
        result.Revisions = revisions;
        result.ReviewQueue = RiskScorer.OrderQueue(batch, findings);
        return result;
    }

    private static bool HasSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dxf" or ".dwg" or ".pdf";

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}

public class ImportResult
{
    public Batch Batch { get; set; } = null!;
    public IReadOnlyDictionary<Guid, ExtractionResult> Extractions { get; set; }
        = new Dictionary<Guid, ExtractionResult>();
    public List<Finding> Findings { get; set; } = new();
    public List<DocumentRevision> Revisions { get; set; } = new();
    public IReadOnlyList<(DrawingFile File, int Score)> ReviewQueue { get; set; } = Array.Empty<(DrawingFile, int)>();
}
