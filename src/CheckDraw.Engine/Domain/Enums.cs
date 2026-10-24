namespace CheckDraw.Engine.Domain;

/// <summary>Estados de conferência do profissional (RF-05).</summary>
public enum CheckState
{
    /// <summary>Sem marcação: não conferido nesta revisão. Não equivale a aprovado.</summary>
    NotChecked = 0,
    /// <summary>Amarelo: conferido e considerado correto pelo profissional.</summary>
    CheckedOk = 1,
    /// <summary>Vermelho: erro identificado, correção necessária.</summary>
    NeedsCorrection = 2,
    /// <summary>Laranja: revalidação necessária após mudança ou efeito indireto.</summary>
    NeedsRevalidation = 3
}

public enum FindingSeverity { Info = 0, Warning = 1, Error = 2, Critical = 3 }

/// <summary>Origem do apontamento: regra determinística ou sugestão de IA.</summary>
public enum FindingSource { Rule = 0, Ai = 1 }

/// <summary>Objetivo = comprovado por regra; Suspeita = exige análise humana.</summary>
public enum FindingKind { Objective = 0, Suspicion = 1 }

/// <summary>Resultado da identificação do documento pelo carimbo.</summary>
public enum IdentificationStatus
{
    /// <summary>Identificado por padrão de carimbo configurado.</summary>
    Identified = 0,
    /// <summary>Fora do padrão ou ambíguo: incerteza declarada, nunca dado inventado (CA-02).</summary>
    Unidentified = 1,
    /// <summary>Interpretado por IA: aguarda confirmação humana.</summary>
    AiSuggested = 2
}

/// <summary>Cenário de entrega documental do contrato.</summary>
public enum DeliveryScenario
{
    /// <summary>A: somente desenho (DWG/DXF). PDF não é exigido.</summary>
    DrawingOnly = 0,
    /// <summary>B: desenho + PDF. Ambos validados e comparados.</summary>
    DrawingPlusPdf = 1,
    /// <summary>C: desenho + 3D se contratado (PDF opcional).</summary>
    DrawingPlus3d = 2
}

/// <summary>Finalidade de liberação de uma revisão.</summary>
public enum ReleasePurpose { Fabrication = 0, Assembly = 1, Consultation = 2 }

/// <summary>Saúde do arquivo recebido (RF-01).</summary>
public enum FileHealth
{
    Ok = 0,
    /// <summary>Arquivo ilegível/corrompido: sinalizado sem interromper o lote (CA-01).</summary>
    Corrupt = 1,
    /// <summary>Mesmo hash de outro arquivo do lote.</summary>
    Duplicate = 2
}

/// <summary>Decisão de liberação controlada (RF-06).</summary>
public enum ReleaseDecision { Allowed = 0, Blocked = 1 }
