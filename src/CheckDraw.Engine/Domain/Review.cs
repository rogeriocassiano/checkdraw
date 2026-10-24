namespace CheckDraw.Engine.Domain;

/// <summary>Ciclo de vida do desenho (modelo rev_cad).</summary>
public enum DrawingLifecycle { Pending = 0, Extracting, Analyzing, Analyzed, UnderReview, Approved, ApprovedRemarks, Rejected }

/// <summary>Veredito humano sobre um apontamento.</summary>
public enum ReviewStatus { Open = 0, Confirmed, Dismissed, Corrected }

/// <summary>Ação do revisor registrada por apontamento — vira dado de aprendizado.</summary>
public class FindingReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FindingId { get; set; }
    public string Action { get; set; } = string.Empty;   // confirmed|dismissed|corrected
    public string? Comment { get; set; }
    public string? CorrectedValue { get; set; }
    public string? Reviewer { get; set; }
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Par de feedback IA×humano — alimenta o aprendizado por projeto (RAG).</summary>
public class AiFeedbackPair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? FileName { get; set; }
    public string? ChecklistCode { get; set; }
    public string AiOutput { get; set; } = string.Empty;
    public string HumanAction { get; set; } = string.Empty;
    public string? HumanCorrection { get; set; }
    public bool WasCorrect { get; set; }
    public string? Reviewer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Template de checklist (sistema: ABNT/ISO — instância editável vem depois).</summary>
public class ChecklistTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? NormCode { get; set; }
    public bool IsSystem { get; set; }
}
