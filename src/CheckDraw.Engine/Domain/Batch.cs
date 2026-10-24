namespace CheckDraw.Engine.Domain;

/// <summary>Lote de documentos recebido de um fornecedor (RF-01).</summary>
public class Batch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Supplier { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public Guid ContractProfileId { get; set; }
    public ContractProfile? Contract { get; set; }

    public List<DrawingFile> Files { get; set; } = new();
}

/// <summary>Arquivo recebido: metadados, saúde, extração e identidade.</summary>
public class DrawingFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public Batch? Batch { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public FileHealth Health { get; set; } = FileHealth.Ok;
    public string? HealthDetail { get; set; }

    /// <summary>Identidade lida do carimbo (quando identificada).</summary>
    public DocumentIdentity Identity { get; set; } = new();

    /// <summary>Stem do PDF associado por nome (associação real de campos vem depois).</summary>
    public string? AssociatedPdfFile { get; set; }

    /// <summary>Ciclo de vida: Pending→Analyzed→UnderReview→Approved/Rejected.</summary>
    public DrawingLifecycle Lifecycle { get; set; } = DrawingLifecycle.Pending;
}

/// <summary>Dados de identificação extraídos do carimbo.</summary>
public class DocumentIdentity
{
    public string? Code { get; set; }
    public string? Revision { get; set; }
    public string? Title { get; set; }
    public string? Discipline { get; set; }
    public string? Format { get; set; }

    public IdentificationStatus Status { get; set; } = IdentificationStatus.Unidentified;

    /// <summary>Padrão de carimbo que casou (ou tentativa de IA).</summary>
    public string? MatchedProfile { get; set; }

    /// <summary>Evidência textual usada na identificação.</summary>
    public string? EvidenceText { get; set; }
}
