using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckDraw.Engine.Ai;

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

    private async Task<AiSuggestion> CallAsync(string prompt, byte[] image, CancellationToken ct, byte[]? secondImage = null)
    {
        if (!Available)
            return new AiSuggestion { Error = "GEMINI_API_KEY não configurada" };
        try
        {
            var parts = new List<object> { new { text = prompt } };
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
