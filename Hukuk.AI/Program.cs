using Hukuk.AI.Data;
using Hukuk.AI.Retrieval;
using Microsoft.SemanticKernel;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration; // Development'ta user secrets otomatik okunur

// appsettings.json'daki boş değerler sadece yer tutucu; asıl değerler user secrets'ta.
string Required(string key) => configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} ayarı bulunamadı (user secrets).");

builder.Services.AddDbContext<AppDbContext>(o => o.UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")));

// Chunk'larla aynı embedding modeli ve boyut; farklı olursa vektörler karşılaştırılamaz.
#pragma warning disable SKEXP0010
builder.Services.AddKernel()
    .AddAzureOpenAIChatCompletion(
        deploymentName: Required("AI:ChatDeploymentName"),
        endpoint: Required("AI:AzureOpenAIEndpoint"),
        apiKey: Required("AI:AzureOpenAIKey"))
    .AddAzureOpenAIEmbeddingGenerator(
        deploymentName: Required("AI:EmbeddingDeploymentName"),
        endpoint: Required("AI:AzureOpenAIEndpoint"),
        apiKey: Required("AI:AzureOpenAIKey"),
        dimensions: 1536);
#pragma warning restore SKEXP0010

// Redis: tek bağlantı (multiplexer) hem HybridCache L2'si hem semantic cache için.
var redis = await ConnectionMultiplexer.ConnectAsync(Required("ConnectionStrings:Redis"));
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddStackExchangeRedisCache(o => o.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(redis));
builder.Services.AddHybridCache(); // kayıtlı IDistributedCache'i (Redis) L2 olarak kullanır

// Aday eşiği .80 (Evaluation "-- cache", 2026-09-29): aday + LLM doğrulayıcı ile paraphrase hit %78, tuzak sızan 0.
// Farklı eval sorularının birbirine maks benzerliği .759: alakasız sorular doğrulayıcıya (+~0.9 sn) gitmez.
builder.Services.AddSingleton(new SemanticCacheOptions(configuration.GetValue<double>("SemanticCache:MinSimilarity")));
builder.Services.AddSingleton<CacheMatchVerifier>();
builder.Services.AddSingleton<SemanticAnswerCache>();
builder.Services.AddSingleton<QueryEmbedder>();
builder.Services.AddSingleton<InflightAnswers>(); // süreç genelinde tek: aynı anda gelen aynı soruları birleştirir

// DbContext scoped olduğu için ona bağlı her şey de scoped.
builder.Services.AddScoped<Retriever>();
builder.Services.AddScoped<KnowledgeSearch>();
builder.Services.AddScoped<KnowledgeBasePlugin>();
builder.Services.AddScoped<LegalAnswerService>();
builder.Services.AddScoped<CachedLegalAnswerService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.GetRequiredService<SemanticAnswerCache>().EnsureIndexAsync();
await WarmUpAsync(app.Services);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.MapControllers();

app.Run();

// Isınma: API açıldıktan sonraki ilk soru 5.1 sn'de ilk parçayı veriyordu (ısınmış hali 1.8 sn); farkın çoğu
// EF'in ilk sorgusu (model kurulumu + bağlantı havuzu, ~1.7 sn) ve Azure/Redis bağlantı kurulumu. Aynı yolu açılışta
// bir kez çalıştırıyoruz: embedding (Redis'te cache'li, sonraki açılışlarda Azure'a gitmez) + vektör + madde araması
// + semantic cache araması. Başarısız olursa API yine açılır, sadece ilk istek yavaş olur.
static async Task WarmUpAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await using var scope = services.CreateAsyncScope();
        const string question = "TBK md. 344 kira artışı"; // madde atfı: doğrudan madde getirme sorgusu da ısınsın
        var vector = await scope.ServiceProvider.GetRequiredService<QueryEmbedder>().EmbedAsync(question);
        await scope.ServiceProvider.GetRequiredService<SemanticAnswerCache>().NearestAsync(vector);
        await scope.ServiceProvider.GetRequiredService<KnowledgeSearch>().SearchAsync(question);
        logger.LogInformation("Isınma tamamlandı: {Ms} ms", sw.ElapsedMilliseconds);
    }
    catch (Exception e)
    {
        logger.LogWarning(e, "Isınma başarısız, API yine de açılıyor");
    }
}
