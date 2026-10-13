using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hukuk.AI.Retrieval;

public record WebResult(string Title, string Url, string Content);

// Tavily arama API'si (karar 2026-10-06): LLM için temizlenmiş metin parçaları döner, sayfayı ayrıca indirmek gerekmez.
public class WebSearchClient(HttpClient http)
{
    public const int MaxResults = 5;
    private const int MaxContentLength = 1500; // sonuç başına; tek bir uzun sayfa bağlamı doldurmasın

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void Configure(HttpClient http, string apiKey)
    {
        http.BaseAddress = new Uri("https://api.tavily.com/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<WebResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("search",
            new SearchRequest(query, MaxResults, "basic"), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken);
        return (body?.Results ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Content))
            .Select(r => new WebResult(r.Title ?? r.Url, r.Url, r.Content.Length > MaxContentLength ? r.Content[..MaxContentLength] : r.Content))
            .ToList();
    }

    private record SearchRequest(string Query, int MaxResults, string SearchDepth);
    private record SearchResponse(List<SearchResult>? Results);
    private record SearchResult(string? Title, string Url, string Content);
}
