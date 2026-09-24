using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Hukuk.AI.Data;

// Sadece "dotnet ef" komutları için (migration oluşturma / uygulama).
// Bağlantı dizesi koda yazılmaz: HUKUKAI_DB ortam değişkeni veya user secrets'taki ConnectionStrings:DefaultConnection.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("HUKUKAI_DB")
            ?? new ConfigurationBuilder().AddUserSecrets<DesignTimeDbContextFactory>().Build().GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Bağlantı dizesi yok: HUKUKAI_DB ortam değişkenini veya user secrets'ta ConnectionStrings:DefaultConnection değerini tanımlayın.");

        var options = new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(connectionString).Options;
        return new AppDbContext(options);
    }
}
