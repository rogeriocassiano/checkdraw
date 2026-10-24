using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Ai;

/// <summary>
/// IA complementar (Gemini e futuros provedores). Nunca decide:
/// todo resultado vira Finding com Source=Ai, Kind=Suspicion, aguardando humano.
/// Contratos sem AllowsExternalAi nunca chamam esta interface.
/// </summary>
public interface IAiAnalyzer
{
    string ProviderName { get; }

    /// <summary>Fallback de carimbo fora do padrão: interpreta imagem/recorte e sugere identidade.</summary>
    Task<AiSuggestion> InterpretTitleBlockAsync(byte[] imageOrSnippet, CancellationToken ct = default);

    /// <summary>Comparação semântica Rev×Rev: aponta o que mudou e onde (evidência p/ revalidação).</summary>
    Task<AiSuggestion> CompareRevisionsAsync(byte[] previousRender, byte[] currentRender, CancellationToken ct = default);
}

public class AiSuggestion
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double? Confidence { get; set; }

    /// <summary>Campo → valor sugerido (ex.: "code"→"DWG-012", "changes"→"...").</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>Descrição da evidência usada pela IA.</summary>
    public string? Evidence { get; set; }
}
