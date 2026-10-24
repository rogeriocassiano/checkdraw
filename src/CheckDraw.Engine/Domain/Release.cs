namespace CheckDraw.Engine.Domain;

/// <summary>
/// Registro de uma revisão de um documento (código). Mantém revisão recebida,
/// mais recente conhecida e autorizada por finalidade (RF-06).
/// </summary>
public class DocumentRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;

    public Guid SourceFileId { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Estado de conferência do profissional nesta revisão.</summary>
    public CheckState State { get; set; } = CheckState.NotChecked;

    /// <summary>
    /// Autorização registrada por finalidade. Revisão recebida NÃO é autorizada
    /// por ser mais recente (CA-04) — só por decisão humana auditada.
    /// </summary>
    public bool AuthorizedForFabrication { get; set; }
    public bool AuthorizedForAssembly { get; set; }
    public bool AuthorizedForConsultation { get; set; }

    public bool IsAuthorizedFor(ReleasePurpose purpose) => purpose switch
    {
        ReleasePurpose.Fabrication => AuthorizedForFabrication,
        ReleasePurpose.Assembly => AuthorizedForAssembly,
        _ => AuthorizedForConsultation
    };
}

/// <summary>Resultado de uma tentativa de emissão controlada (auditável).</summary>
public class ReleaseAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public ReleasePurpose Purpose { get; set; }
    public ReleaseDecision Decision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
    public string? AttemptedBy { get; set; }
}
