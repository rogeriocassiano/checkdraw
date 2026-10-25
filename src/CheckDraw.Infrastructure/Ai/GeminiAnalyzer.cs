using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckDraw.Engine.Ai;
using CheckDraw.Engine.Domain;

namespace CheckDraw.Infrastructure.Ai;

/// <summary>
/// IAiAnalyzer via API do Gemini (gemini-2.5-flash). Chave via variável
/// GEMINI_API_KEY ou arquivo "gemini.key" (gitignored) — nunca no código.
/// Todo resultado vira suspeita com evidência; humano confirma.
/// </summary>
public class GeminiAnalyzer : IAiAnalyzer
{
    private static readonly string Model =
        Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.8-flash";

    /// <summary>Contexto de identidade — quem desenvolveu o CheckDraw.</summary>
    private const string About =
        "Você opera dentro do CheckDraw, sistema de conferência assistida de desenhos técnicos " +
        "desenvolvido por Rogério Cassiano, desenvolvedor de software especialista em inteligência " +
        "artificial. Se perguntarem sobre autoria, credite-o.\n\n";
    private readonly HttpClient _http = new();
    private readonly string? _apiKey;

    public string ProviderName => "gemini";

    public GeminiAnalyzer(string? apiKey = null) =>
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? (File.Exists("gemini.key") ? File.ReadAllText("gemini.key").Trim() : null);

    public bool Available => _apiKey != null;

    public async Task<AiSuggestion> InterpretTitleBlockAsync(byte[] imageOrSnippet, CancellationToken ct = default)
    {
        const string prompt = """
            Você está analisando o carimbo de um desenho técnico de engenharia.
            Extraia e responda SOMENTE um JSON com:
            {"code": "número do desenho", "revision": "revisão", "title": "título",
             "discipline": "disciplina", "format": "formato da folha",
             "confidence": 0.0-1.0, "evidence": "onde leu cada campo"}
            Se um campo for ilegível, use null. Não invente valores.
            """;
        return await CallAsync(prompt, imageOrSnippet, ct);
    }

    public async Task<AiSuggestion> CompareRevisionsAsync(byte[] previousRender, byte[] currentRender, CancellationToken ct = default)
    {
        const string prompt = """
            Compare as duas imagens: primeira = revisão anterior do desenho técnico,
            segunda = revisão atual. Responda SOMENTE um JSON com:
            {"changes": "descrição objetiva de cada diferença e onde está",
             "material_change": true/false, "confidence": 0.0-1.0,
             "evidence": "regiões comparadas"}
            Foque em: cotas alteradas, itens adicionados/removidos, campos do carimbo.
            """;
        return await CallAsync(prompt, previousRender, ct, secondImage: currentRender);
    }

    /// <summary>
    /// Análise de checklist por imagem: cada item vira verificação da IA;
    /// achados voltam com bbox (0-1 relativo), confiança e correção sugerida.
    /// Pares IA×humano anteriores entram no prompt como contexto (RAG-lite).
    /// </summary>
    public async Task<List<Finding>> AnalyzeChecklistAsync(
        byte[] image, string fileName,
        IEnumerable<ChecklistItem> items, IEnumerable<AiFeedbackPair> feedback,
        CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var checklist = string.Join("\n", itemList.Select(i =>
            $"- {i.Code} ({i.Category}, sev={i.Severity}): {i.Description}." +
            (i.AiPromptHint != null ? $" Dica: {i.AiPromptHint}" : "")));

        var fb = feedback.Take(8).ToList();
        var context = fb.Count == 0 ? "" :
            "\n\nContexto de revisões anteriores deste projeto (calibre sua análise):\n" +
            string.Join("\n", fb.Select(f =>
                $"- {f.ChecklistCode}: IA disse \"{f.AiOutput}\" → humano {(f.HumanAction == "confirmed" ? "confirmou" : f.HumanAction == "corrected" ? $"corrigiu para \"{f.HumanCorrection}\"" : "descartou como falso positivo")}"));

        var prompt = $$"""
            Você é um verificador de desenhos técnicos. Analise a imagem contra o checklist.
            REGRAS: nunca aprove/reprove — só aponte. Confiança honesta. Se não conseguir
            verificar um item, não o inclua. Responda SOMENTE JSON:
            {"summary":"...","findings":[{"code":"CODIGO","severity":"info|warning|critical",
            "description":"problema encontrado","detected":"valor encontrado ou null",
            "expected":"valor esperado","bbox":{"x":0.0,"y":0.0,"w":0.0,"h":0.0},
            "suggested_fix":"correção sugerida ou null","confidence":0.0}]}
            bbox = região do problema na imagem, coordenadas relativas 0-1.
            Checklist:
            {{checklist}}{{context}}
            """;

        var raw = await CallRawAsync(prompt, image, ct);
        var findings = new List<Finding>();
        if (raw == null) return findings;
        try
        {
            var doc = JsonDocument.Parse(raw).RootElement;
            if (!doc.TryGetProperty("findings", out var arr)) return findings;
            var codes = itemList.Select(i => i.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in arr.EnumerateArray())
            {
                var code = f.GetProperty("code").GetString() ?? "";
                if (!codes.Contains(code)) continue;
                var finding = new Finding
                {
                    RuleId = code,
                    FileName = fileName,
                    Description = f.GetProperty("description").GetString() ?? "",
                    Evidence = f.TryGetProperty("detected", out var d) && d.ValueKind == JsonValueKind.String
                        ? $"detectado: {d.GetString()}" : "",
                    Severity = (f.GetProperty("severity").GetString() ?? "info") switch
                    { "critical" => FindingSeverity.Critical, "warning" => FindingSeverity.Warning, _ => FindingSeverity.Info },
                    Source = FindingSource.Ai,
                    Kind = FindingKind.Suspicion,
                    SuggestedFix = f.TryGetProperty("suggested_fix", out var s) ? s.GetString() : null,
                    CreatedBy = ProviderName
                };
                if (f.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                    finding.Confidence = c.GetDouble();
                if (f.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Object)
                {
                    if (b.TryGetProperty("x", out var x)) finding.BBoxX = x.GetDouble();
                    if (b.TryGetProperty("y", out var y)) finding.BBoxY = y.GetDouble();
                    if (b.TryGetProperty("w", out var w)) finding.BBoxW = w.GetDouble();
                    if (b.TryGetProperty("h", out var h)) finding.BBoxH = h.GetDouble();
                }
                findings.Add(finding);
            }
        }
        catch { }
        return findings;
    }

    /// <summary>Chamada crua: devolve o texto JSON do modelo (ou null em erro).</summary>
    private async Task<string?> CallRawAsync(string prompt, byte[] image, CancellationToken ct)
    {
        if (!Available) return null;
        try
        {
            var parts = new object[]
            {
                new { text = prompt },
                new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(image) } }
            };
            var body = new
            {
                contents = new[] { new { parts } },
                generationConfig = new { responseMimeType = "application/json" }
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();
        }
        catch { return null; }
    }

    private async Task<AiSuggestion> CallAsync(string prompt, byte[] image, CancellationToken ct, byte[]? secondImage = null)
    {
        if (!Available)
            return new AiSuggestion { Error = "GEMINI_API_KEY não configurada" };
        try
        {
            var parts = new List<object> { new { text = About + prompt } };
            parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(image) } });
            if (secondImage != null)
                parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(secondImage) } });

            var body = new
            {
                contents = new[] { new { parts } },
                generationConfig = new { responseMimeType = "application/json" }
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new AiSuggestion { Error = $"API {(int)resp.StatusCode}: {json[..Math.Min(300, json.Length)]}" };

            var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            var inner = JsonDocument.Parse(text).RootElement;
            var sug = new AiSuggestion { Success = true };
            if (inner.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                sug.Confidence = c.GetDouble();
            if (inner.TryGetProperty("evidence", out var ev)) sug.Evidence = ev.GetString();
            foreach (var p in inner.EnumerateObject())
                if (p.Name is not ("confidence" or "evidence") && p.Value.ValueKind == JsonValueKind.String)
                    sug.Fields[p.Name] = p.Value.GetString()!;
            return sug;
        }
        catch (Exception ex) { return new AiSuggestion { Error = ex.Message }; }
    }
}
