namespace CheckDraw.Engine.Domain;

/// <summary>Perfil versionado de contrato: o que se exige da entrega (RF-04).</summary>
public class ContractProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;

    public DeliveryScenario Delivery { get; set; } = DeliveryScenario.DrawingOnly;

    /// <summary>Se o contrato autoriza envio de dados a IA externa (ex.: Gemini).</summary>
    public bool AllowsExternalAi { get; set; }

    /// <summary>Padrões de carimbo reconhecidos neste contrato.</summary>
    public List<TitleBlockProfile> TitleBlocks { get; set; } = new();

    /// <summary>Campos de carimbo obrigatórios (ex.: código, revisão, título).</summary>
    public List<string> RequiredTitleFields { get; set; } = new() { "code", "revision" };

    /// <summary>Colunas obrigatórias na lista de materiais (RF-03).</summary>
    public List<string> RequiredBomColumns { get; set; } = new() { "item", "code", "qty", "description" };
}

/// <summary>
/// Configuração de um padrão de carimbo: como o extrator mapeia entidades a campos.
/// Sem configuração correspondente, o documento é "não identificado" — nunca inventado.
/// </summary>
public class TitleBlockProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome do bloco (INSERT) que representa o carimbo neste padrão.</summary>
    public string? BlockName { get; set; }

    /// <summary>Mapeia campo lógico → tag de atributo do bloco. Ex.: "code"→"CODIGO".</summary>
    public Dictionary<string, string> AttributeMap { get; set; } = new();
}

/// <summary>Entrada da lista mestra: documento esperado pelo contrato.</summary>
public class MasterListEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string ExpectedRevision { get; set; } = string.Empty;
    public string? Title { get; set; }
}
