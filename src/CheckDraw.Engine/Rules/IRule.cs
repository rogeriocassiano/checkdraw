using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Engine.Rules;

/// <summary>Contexto entregue às regras: lote já importado, extrações e lista mestra.</summary>
public class RuleContext
{
    public Batch Batch { get; set; } = null!;
    public ContractProfile Contract { get; set; } = null!;

    /// <summary>Extração por arquivo (somente arquivos lidos com sucesso).</summary>
    public IReadOnlyDictionary<Guid, ExtractionResult> Extractions { get; set; }
        = new Dictionary<Guid, ExtractionResult>();

    public IReadOnlyList<MasterListEntry> MasterList { get; set; } = Array.Empty<MasterListEntry>();
}

public interface IRule
{
    string RuleId { get; }
    IEnumerable<Finding> Evaluate(RuleContext ctx);
}
