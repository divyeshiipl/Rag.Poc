using Microsoft.EntityFrameworkCore;
using Rag.Poc.Data;
using Rag.Poc.Models;
using Rag.Poc.Services;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<RagDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("RagDatabase"),
        o => o.UseVector()));

builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DocumentDiscoveryService>();
builder.Services.AddScoped<DocumentSyncService>();
builder.Services.AddScoped<DocumentIngestionService>();
builder.Services.AddScoped<DocumentExtractor>();
builder.Services.AddScoped<DocumentChunker>();
builder.Services.AddScoped<DocumentSearchService>();
builder.Services.AddScoped<RagAnswerService>();

builder.Services.AddSwaggerGen();

builder.Services.AddControllersWithViews();

var bgeModelPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "Models", "bge-small-en-v1.5", "model.onnx");

var bgeVocabularyPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "Models", "bge-small-en-v1.5", "vocab.txt");

builder.Services.AddSingleton(new BgeSmallOptions
{
    ModelPath = bgeModelPath,
    VocabularyPath = bgeVocabularyPath,
    MaxTokens = 512
});

builder.Services.AddSingleton<IEmbeddingService, BgeSmallEmbeddingService>();

builder.Services.AddHttpClient<ILanguageModelService, OllamaLanguageModelService>(
    client => client.BaseAddress = new Uri("http://localhost:11434"));

builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
    var canConnect = await db.Database.CanConnectAsync();
    Console.WriteLine($"Database connected: {canConnect}");
}


app.UseStaticFiles();          
app.UseRouting();             
app.UseHttpsRedirection();     
app.UseAuthorization();       

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();         
app.MapControllerRoute(        
    name: "default",
    pattern: "{controller=Ui}/{action=Index}/{id?}");

app.Run();