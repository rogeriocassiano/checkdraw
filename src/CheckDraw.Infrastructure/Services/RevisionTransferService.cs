using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Engine.Transfer;
using CheckDraw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckDraw.Infrastructure.Services;

/// <summary>
/// Transfere as marcas de conferência de uma revisão anterior para a nova
/// revisão do mesmo documento, avaliando cada região (RF-06).
/// </summary>
public class RevisionTransferService
{
    private readonly List<IDocumentExtractor> _extractors;

    public RevisionTransferService(IEnumerable<IDocumentExtractor> extractors) =>
        _extractors = extractors.ToList();

    public async Task<TransferResult> TransferAsync(string dbPath, string code, string fromRev, string toRev)
    {
        await using var db = new CheckDrawDbContext(dbPath);

        var files = await db.Files.Where(f => f.Identity.Code == code).ToListAsync();
        var from = files.FirstOrDefault(f => f.Identity.Revision == fromRev);
        var to = files.FirstOrDefault(f => f.Identity.Revision == toRev);
        if (from == null) return new TransferResult { Error = $"Arquivo Rev.{fromRev} de '{code}' não encontrado" };
        if (to == null) return new TransferResult { Error = $"Arquivo Rev.{toRev} de '{code}' não encontrado" };

        var exFrom = Extract(from.FullPath);
        var exTo = Extract(to.FullPath);
        if (exFrom?.Success != true) return new TransferResult { Error = $"Falha ao ler {from.FileName}" };
        if (exTo?.Success != true) return new TransferResult { Error = $"Falha ao ler {to.FileName}" };

        var marks = await db.CheckMarks.Where(c => c.FileName == from.FileName).ToListAsync();
        var result = new TransferResult();

        foreach (var m in marks)
        {
            var newState = MarkTransfer.Evaluate(m, exFrom, exTo);
            var transfer = new CheckMark
            {
                FileName = to.FileName,
                DocumentCode = code,
                Revision = toRev,
                X = m.X, Y = m.Y, W = m.W, H = m.H,
                State = newState,
                User = m.User,
                Note = $"Transferida de Rev.{fromRev}: " +
                       (newState == m.State ? "inalterada" : "conteúdo mudou — revalidar")
            };
            db.CheckMarks.Add(transfer);

            if (newState == m.State) result.Preserved++;
            else result.Revalidations++;
        }

        var rev = await db.Revisions.FirstOrDefaultAsync(r => r.Code == code && r.Revision == toRev);
        if (rev != null && result.Revalidations > 0)
            rev.State = CheckState.NeedsRevalidation;

        db.AuditLog.Add(new AuditEvent
        {
            Action = "transfer",
            Detail = $"{code} Rev.{fromRev}→Rev.{toRev}: {result.Preserved} preservadas, {result.Revalidations} a revalidar",
            User = "system"
        });
        await db.SaveChangesAsync();
        return result;
    }

    private ExtractionResult? Extract(string path) =>
        _extractors.FirstOrDefault(e => e.CanRead(path))?.Extract(path);
}

public class TransferResult
{
    public int Preserved { get; set; }
    public int Revalidations { get; set; }
    public string? Error { get; set; }
}
