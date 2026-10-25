using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheckDraw.Infrastructure.Aps;

/// <summary>
/// Autodesk Platform Services: token 2-legged → upload OSS → tradução (SVF2)
/// → manifesto. Resolve DWG na nuvem (extração + viewer) sem AutoCAD/ODA.
/// Env: APS_CLIENT_ID, APS_CLIENT_SECRET.
/// </summary>
public class ApsService
{
    private const string Base = "https://developer.api.autodesk.com";
    private readonly HttpClient _http = new();
    private readonly string? _id;
    private readonly string? _secret;

    public ApsService(string? id = null, string? secret = null)
    {
        _id = id ?? Environment.GetEnvironmentVariable("APS_CLIENT_ID");
        _secret = secret ?? Environment.GetEnvironmentVariable("APS_CLIENT_SECRET");
    }

    public bool Available => _id != null && _secret != null;

    private async Task<string> TokenAsync()
    {
        var resp = await _http.PostAsync($"{Base}/authentication/v2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _id!,
                ["client_secret"] = _secret!,
                ["grant_type"] = "client_credentials",
                ["scope"] = "data:read data:write data:create bucket:create bucket:read"
            }));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Envia um arquivo para o OSS e devolve o URN base64 para tradução.</summary>
    public async Task<string> UploadAsync(string bucket, string objectName, byte[] content, CancellationToken ct = default)
    {
        var token = await TokenAsync();

        // bucket precisa existir (idempotente — ignora 409 "já existe")
        using var mk = new HttpRequestMessage(HttpMethod.Post, $"{Base}/oss/v2/buckets")
        {
            Content = JsonContent.Create(new { bucketKey = bucket, policyKey = "transient" })
        };
        mk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var mkResp = await _http.SendAsync(mk, ct);
        if ((int)mkResp.StatusCode != 409) mkResp.EnsureSuccessStatusCode();

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{Base}/oss/v2/buckets/{bucket}/objects/{Uri.EscapeDataString(objectName)}/signeds3upload")
        { Content = JsonContent.Create(new { minutesExpiration = 10 }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var signed = await req.SendAndRead<JsonElement>(_http, ct);
        var uploadKey = signed.GetProperty("uploadKey").GetString()!;
        var signedUrl = signed.GetProperty("urls")[0].GetString()!;

        using var put = new HttpRequestMessage(HttpMethod.Put, signedUrl) { Content = new ByteArrayContent(content) };
        await put.SendAndRead<JsonElement>(_http, ct);

        using var fin = new HttpRequestMessage(HttpMethod.Post,
            $"{Base}/oss/v2/buckets/{bucket}/objects/{Uri.EscapeDataString(objectName)}/signeds3upload")
        { Content = JsonContent.Create(new { uploadKey }) };
        fin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var obj = await fin.SendAndRead<JsonElement>(_http, ct);
        var objectId = obj.GetProperty("objectId").GetString()!;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(objectId))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Submete tradução para SVF2 (viewer) — retorna quando 'success'.</summary>
    public async Task<string> TranslateAndWaitAsync(string urn, CancellationToken ct = default)
    {
        var token = await TokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/modelderivative/v2/designdata/job")
        {
            Content = JsonContent.Create(new
            {
                input = new { urn },
                output = new { formats = new[] { new { type = "svf2", views = new[] { "2d", "3d" } } } }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await req.SendAndRead<JsonElement>(_http, ct);

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(5000, ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get,
                $"{Base}/modelderivative/v2/designdata/{urn}/manifest");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var m = await poll.SendAndRead<JsonElement>(_http, ct);
            var status = m.GetProperty("status").GetString();
            if (status == "success") return m.ToString();
            if (status == "failed") throw new InvalidOperationException("tradução APS falhou: " + m);
        }
        throw new TimeoutException("tradução APS demorou demais");
    }
}

static class ApsHttp
{
    public static async Task<JsonElement> SendAndRead<T>(this HttpRequestMessage req, HttpClient http, CancellationToken ct)
    {
        var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"APS {(int)resp.StatusCode}: {text[..Math.Min(300, text.Length)]}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
