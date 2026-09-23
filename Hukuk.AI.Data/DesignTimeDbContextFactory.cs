using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hukuk.AI.Data;

// Sadece "dotnet ef" komutları için (migration oluşturma / uygulama)
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("HUKUKAI_DB")
            ?? "Host=localhost;Port=5433;Database=HukukAIDb;Username=admin;Password=SecretPassword123!";

        var options = new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(connectionString).Options;
        return new AppDbContext(options);
    }
}
