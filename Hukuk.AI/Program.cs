using System.Threading.RateLimiting;
using Hukuk.AI.Data;
using Azure;
using Azure.AI.DocumentIntelligence;
using Hukuk.AI.Auth;
using Hukuk.AI.Controllers;
using Hukuk.AI.Credits;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Documents;
using Hukuk.AI.Drafting;
using Hukuk.AI.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.ML.Tokenizers;
using Hukuk.AI.Retrieval;
using Microsoft.SemanticKernel;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration; // Development'ta user secrets otomatik okunur

// appsettings.json'daki boş değerler sadece yer tutucu; asıl değerler user secrets'ta.
string Required(string key) => configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} ayarı bulunamadı (user secrets).");

builder.Services.AddDbContext<AppDbContext>(o => o.UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")));

// Kimlik (kararlar 2026-10-13): ASP.NET Core Identity (parola hash'i, kilitleme) + JWT access token + refresh token.
// AddIdentityCore: çerez/rol/arayüz yok, sadece UserManager.
builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
    })
    .AddEntityFrameworkStores<AppDbContext>();

var jwt = new JwtOptions(Required("Auth:Issuer"), Required("Auth:Audience"), Required("Auth:JwtKey"),
    configuration.GetValue<int>("Auth:AccessTokenMinutes"), configuration.GetValue<int>("Auth:RefreshTokenDays"));
builder.Services.AddSingleton(jwt);
builder.Services.AddScoped<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false; // "sub" claim'i ClaimTypes.NameIdentifier'a çevrilmesin
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.SigningKey,
        ClockSkew = TimeSpan.FromSeconds(30), // varsayılan 5 dk, 15 dk'lık token için fazla
    };
});
builder.Services.AddAuthorization();

builder.Services.AddSingleton(configuration.GetSection("Credits").Get<CreditOptions>() ?? throw new InvalidOperationException("Credits ayarı bulunamadı."));
builder.Services.AddScoped<CreditService>();

// İstek sınırı: sabit pencere (dakikada N). Girişli uçlarda kullanıcı başına, kayıt/giriş uçlarında IP başına.
// Sayaçlar süreç belleğinde: birden çok kopya çalışırsa her kopya ayrı sayar.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    RateLimitPartition<string> PerMinute(string key, string setting) => RateLimitPartition.GetFixedWindowLimiter(key,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue<int>(setting), Window = TimeSpan.FromMinutes(1) });
    o.AddPolicy(RateLimitPolicies.User, http => PerMinute(http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous", "RateLimit:UserPerMinute"));
    o.AddPolicy(RateLimitPolicies.Auth, http => PerMinute(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", "RateLimit:AuthPerMinute"));
});

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

// Yüklenen belgeler: Redis'te oturumluk saklama + kuyruk, arka planda OCR → parçalama → embedding.
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<DocumentQueue>();
builder.Services.AddSingleton(new DocumentChunker(TiktokenTokenizer.CreateForModel("text-embedding-3-large"))); // embedding modelinin tokenizer'ı
if (configuration["AI:DocIntelEndpoint"] is { Length: > 0 } docIntelEndpoint)
    builder.Services.AddSingleton<IDocumentReader>(new AzureDocumentReader(
        new DocumentIntelligenceClient(new Uri(docIntelEndpoint), new AzureKeyCredential(Required("AI:DocIntelKey")))));
else
    builder.Services.AddSingleton<IDocumentReader, NotConfiguredDocumentReader>(); // API yine açılır, belge işleme hata verir
builder.Services.AddScoped<RiskReportService>(); // bilgi tabanı aramasına (DbContext) bağlı
builder.Services.AddScoped<DocumentProcessor>();
builder.Services.AddScoped<DocumentAnswerService>();
builder.Services.AddHostedService<DocumentWorker>();

// Sözleşme taslağı: oturum Redis'te, yazım + risk raporuyla denetim.
builder.Services.AddSingleton<DraftStore>();
builder.Services.AddSingleton<DraftIntake>();
builder.Services.AddScoped<DraftWriter>(); // bilgi tabanına (DbContext) ve RiskReportService'e bağlı

// DbContext scoped olduğu için ona bağlı her şey de scoped.
builder.Services.AddScoped<Retriever>();
builder.Services.AddScoped<KnowledgeSearch>();
builder.Services.AddScoped<KnowledgeBasePlugin>();
builder.Services.AddScoped<LegalAnswerService>();
builder.Services.AddScoped<CachedLegalAnswerService>();

// Agent modu: web araması (Tavily) anahtarı yoksa API yine açılır, model sadece bilgi tabanı aracını görür.
if (configuration["AI:TavilyKey"] is { Length: > 0 } tavilyKey)
    builder.Services.AddHttpClient<WebSearchClient>(http => WebSearchClient.Configure(http, tavilyKey));
builder.Services.AddScoped<AgentAnswerService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.GetRequiredService<SemanticAnswerCache>().EnsureIndexAsync();
await app.Services.GetRequiredService<DocumentStore>().EnsureIndexAsync();
await WarmUpAsync(app.Services);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseRateLimiter(); // kimlikten sonra: kullanıcı başına sınır için kullanıcı bilinmeli
app.UseAuthorization();
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
