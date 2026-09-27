using Hukuk.AI.Data;
using Hukuk.AI.Retrieval;
using Microsoft.SemanticKernel;

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

// DbContext scoped olduğu için ona bağlı her şey de scoped.
builder.Services.AddScoped<Retriever>();
builder.Services.AddScoped<QueryRewriter>();
builder.Services.AddScoped<KnowledgeSearch>();
builder.Services.AddScoped<KnowledgeBasePlugin>();
builder.Services.AddScoped<LegalAnswerService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
