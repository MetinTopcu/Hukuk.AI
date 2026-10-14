using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hukuk.AI.Credits;

// Ücretli işlemler; fiyatları appsettings "Credits:Prices" altında aynı adlarla.
public static class CreditOperations
{
    public const string Question = "question";
    public const string Agent = "agent";
    public const string Document = "document"; // yükleme + risk raporu
    public const string DocumentQuestion = "document_question";
    public const string Draft = "draft";
}

public record CreditPackage(string Key, int Credits, decimal Price);

public class CreditOptions
{
    public int SignupGift { get; set; }

    public Dictionary<string, int> Prices { get; set; } = [];

    public string Currency { get; set; } = "TRY";

    public List<CreditPackage> Packages { get; set; } = [];
}

// Kararlar (2026-10-13): işlem başına sabit fiyat; kredi istek başında düşülür (rezerve), işlem başarısız olursa iade
// edilir. Bakiye users.credits kolonunda, her değişiklik aynı transaction'da credit_transactions'a (ledger) yazılır.
public class CreditService(AppDbContext db, CreditOptions options, ILogger<CreditService> logger)
{
    public int Price(string operation) =>
        options.Prices.TryGetValue(operation, out var price) ? price : throw new InvalidOperationException($"Credits:Prices:{operation} tanımlı değil.");

    // İşlem ücretini düşer; dönen değer iade için gereken hareket numarası. Bakiye yetmiyorsa null.
    // Kontrol ve düşüm tek UPDATE'te (WHERE credits >= fiyat): aynı anda gelen iki istek bakiyeyi eksiye düşüremez;
    // "oku, kontrol et, yaz" yapılsaydı ikisi de aynı bakiyeyi okuyup geçebilirdi.
    public async Task<long?> ReserveAsync(Guid userId, string operation, CancellationToken cancellationToken = default)
    {
        var price = Price(operation);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Users.Where(u => u.Id == userId && u.Credits >= price)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits - price), cancellationToken);
        if (updated == 0)
            return null;

        var row = await AddRowAsync(userId, CreditTransactionType.Usage, -price, operation, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return row.Id;
    }

    // Başarısız işlemin ücretini geri verir. Aynı hareket ikinci kez iade edilmez (refund_of_id unique).
    // İstek iptal edilmiş olsa da çalışsın diye CancellationToken almaz.
    public async Task RefundAsync(long chargeId)
    {
        try
        {
            var charge = await db.CreditTransactions.AsNoTracking().SingleAsync(t => t.Id == chargeId && t.Type == CreditTransactionType.Usage);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Users.Where(u => u.Id == charge.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits - charge.Amount));
            await AddRowAsync(charge.UserId, CreditTransactionType.Refund, -charge.Amount, charge.Operation, default, refundOfId: charge.Id);
            await tx.CommitAsync();
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Zaten iade edilmiş: transaction geri alındı, bakiye değişmedi.
            db.ChangeTracker.Clear();
        }
        catch (Exception e)
        {
            // İade hatası asıl hatayı gizlemesin; elle düzeltilebilsin diye loglanır.
            logger.LogError(e, "Kredi iadesi yapılamadı: hareket {ChargeId}", chargeId);
            db.ChangeTracker.Clear();
        }
    }

    // Kayıt hediyesi. Çağıran, kullanıcıyı oluşturduğu transaction'ın içinde çağırır.
    public async Task GrantSignupGiftAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (options.SignupGift <= 0)
            return;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits + options.SignupGift), cancellationToken);
        await AddRowAsync(userId, CreditTransactionType.Signup, options.SignupGift, null, cancellationToken);
    }

    // Ödenen paketin kredisi. Çağıran, ödemeyi "paid" yaptığı transaction'ın içinde çağırır; aynı ödeme ikinci kez
    // gelirse payment_id unique index'i yüzünden hata verir ve transaction geri alınır.
    public async Task AddPurchaseAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        await db.Users.Where(u => u.Id == payment.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits + payment.Credits), cancellationToken);
        await AddRowAsync(payment.UserId, CreditTransactionType.Purchase, payment.Credits, null, cancellationToken, paymentId: payment.Id);
    }

    // Hesap silinirken kalan bakiyeyi sıfırlar. Çağıran, silme transaction'ının içinde çağırır.
    // Silinen hesabın hâlâ geçerli access token'ı (en fazla 15 dk) ücretli işlem yapamasın diye.
    public async Task ForfeitAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var balance = await db.Users.Where(u => u.Id == userId).Select(u => u.Credits).SingleAsync(cancellationToken);
        if (balance == 0)
            return;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, 0), cancellationToken);
        await AddRowAsync(userId, CreditTransactionType.Forfeit, -balance, null, cancellationToken);
    }

    // Açık bir transaction içinde, bakiye UPDATE'inden sonra çağrılır: kullanıcı satırı bu transaction'da kilitli
    // olduğu için okunan bakiye tam bu hareketten sonraki bakiyedir.
    private async Task<CreditTransaction> AddRowAsync(Guid userId, CreditTransactionType type, int amount, string? operation,
        CancellationToken cancellationToken, long? refundOfId = null, Guid? paymentId = null)
    {
        var balance = await db.Users.Where(u => u.Id == userId).Select(u => u.Credits).SingleAsync(cancellationToken);
        var row = new CreditTransaction
        {
            UserId = userId,
            Type = type,
            Amount = amount,
            BalanceAfter = balance,
            Operation = operation,
            RefundOfId = refundOfId,
            PaymentId = paymentId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CreditTransactions.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }
}
