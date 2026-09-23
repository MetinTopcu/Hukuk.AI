using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>()
    .Build();

var endpoint = configuration["AI:AzureOpenAIEndpoint"];
var apiKey = configuration["AI:AzureOpenAIKey"];

var chatModelId = configuration["AI:ModelId"]
    ?? throw new InvalidOperationException("AI:ModelId ayarı bulunamadı.");

// 2. Şefi (Kernel) yaratalım ve ona Azure OpenAI'ı bağlayalım
var builder = Kernel.CreateBuilder();
builder.AddAzureOpenAIChatCompletion(
    deploymentName: chatModelId,
    endpoint: endpoint!,
    apiKey: apiKey!);

var kernel = builder.Build();

Console.WriteLine("Hukuk.AI Başlatılıyor...\n");

var prompt = "Sen kıdemli bir avukatsın. 'Mücbir Sebep' nedir, sadece 1 cümleyle açıkla.";
Console.WriteLine($"Soru: {prompt}");
Console.WriteLine("Cevap bekleniyor...\n");

var result = await kernel.InvokePromptAsync(prompt);

Console.WriteLine($"Hukuk.AI: {result}");
Console.ReadLine();