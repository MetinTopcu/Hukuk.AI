using System.ComponentModel.DataAnnotations;
using Hukuk.AI.Credits;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hukuk.AI.Controllers;

// Giriş yapmış kullanıcının hesabı: bakiye, fiyatlar, kredi hareketleri; KVKK: veri dışa aktarma ve hesap silme.
[Route("api/account")]
public class AccountController(AppDbContext db, CreditOptions options, UserManager<AppUser> users, CreditService credits) : ApiController
{
    public record AccountResponse(string Email, int Credits, Dictionary<string, int> Prices);

    public record TransactionResponse(long Id, string Type, int Amount, int BalanceAfter, string? Operation, DateTimeOffset CreatedAt);

    public record PaymentResponse(Guid Id, string Package, int Credits, decimal Price, string Currency, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

    public record ExportAccount(string Email, DateTimeOffset CreatedAt, int Credits, string? PrivacyVersion, DateTimeOffset? PrivacyAcceptedAt);

    public record ExportResponse(DateTimeOffset ExportedAt, ExportAccount Account, List<TransactionResponse> Transactions, List<PaymentResponse> Payments, string Note);

    public record DeleteRequest([Required, StringLength(100)] string Password);

    [HttpGet]
    public async Task<ActionResult<AccountResponse>> Get(CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == UserId && u.DeletedAt == null).Select(u => new { u.Email, u.Credits }).SingleOrDefaultAsync(cancellationToken);
        return user is null ? Unauthorized() : new AccountResponse(user.Email!, user.Credits, options.Prices);
    }

    // Yeniden eskiye. before: önceki sayfanın son hareket id'si (keyset sayfalama; OFFSET gibi derinleştikçe yavaşlamaz).
    [HttpGet("transactions")]
    public async Task<List<TransactionResponse>> Transactions(long? before, CancellationToken cancellationToken) =>
        await db.CreditTransactions.AsNoTracking()
            .Where(t => t.UserId == UserId && (before == null || t.Id < before))
            .OrderByDescending(t => t.Id)
            .Take(50)
            .Select(t => new TransactionResponse(t.Id, t.Type.ToString(), t.Amount, t.BalanceAfter, t.Operation, t.CreatedAt))
            .ToListAsync(cancellationToken);

    // KVKK md. 11: kullanıcının bizde tuttuğumuz tüm kişisel verisi tek JSON'da. Ödeme sağlayıcısının iç
    // numaraları (token, payment id) kişisel veri değil, dahil edilmez.
    [HttpGet("export")]
    public async Task<ActionResult<ExportResponse>> Export(CancellationToken cancellationToken)
    {
        var account = await db.Users.AsNoTracking().Where(u => u.Id == UserId && u.DeletedAt == null)
            .Select(u => new ExportAccount(u.Email!, u.CreatedAt, u.Credits, u.PrivacyVersion, u.PrivacyAcceptedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
            return Unauthorized();

        var transactions = await db.CreditTransactions.AsNoTracking().Where(t => t.UserId == UserId).OrderBy(t => t.Id)
            .Select(t => new TransactionResponse(t.Id, t.Type.ToString(), t.Amount, t.BalanceAfter, t.Operation, t.CreatedAt))
            .ToListAsync(cancellationToken);
        var payments = await db.Payments.AsNoTracking().Where(p => p.UserId == UserId).OrderBy(p => p.CreatedAt)
            .Select(p => new PaymentResponse(p.Id, p.Package, p.Credits, p.Price, p.Currency, p.Status.ToString(), p.CreatedAt, p.PaidAt))
            .ToListAsync(cancellationToken);

        return new ExportResponse(DateTimeOffset.UtcNow, account, transactions, payments,
            "Sorular kullanıcıya bağlı saklanmaz. Yüklenen belgeler ve taslaklar son kullanımdan en fazla 2 saat sonra silinir.");
    }

    // Hesap silme = anonimleştirme. Kredi hareketleri ve ödemeler mali kayıt olarak kalır ama kullanıcı satırında
    // kişiyi gösteren bir şey kalmaz (e-posta, parola hash'i silinir). Kalan bakiye sıfırlanır, refresh token'lar
    // silinir: hâlâ geçerli access token (en fazla 15 dk) ücretli işlem yapamaz, yenilenemez.
    // Yanlışlıkla/çalınmış token'la silinmesin diye parola tekrar istenir.
    [HttpDelete]
    public async Task<IActionResult> Delete(DeleteRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(UserId.ToString());
        if (user is null || user.DeletedAt is not null)
            return Unauthorized();
        if (!await users.CheckPasswordAsync(user, request.Password))
            return Problem("Parola hatalı.", statusCode: StatusCodes.Status401Unauthorized);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await credits.ForfeitAllAsync(UserId, cancellationToken);
        await db.RefreshTokens.Where(t => t.UserId == UserId).ExecuteDeleteAsync(cancellationToken);
        await db.UserClaims.Where(c => c.UserId == UserId).ExecuteDeleteAsync(cancellationToken);
        await db.UserLogins.Where(l => l.UserId == UserId).ExecuteDeleteAsync(cancellationToken);
        await db.UserTokens.Where(t => t.UserId == UserId).ExecuteDeleteAsync(cancellationToken);

        var deletedName = $"deleted-{UserId:N}";
        await db.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.UserName, deletedName)
            .SetProperty(u => u.NormalizedUserName, deletedName.ToUpperInvariant())
            .SetProperty(u => u.Email, (string?)null)
            .SetProperty(u => u.NormalizedEmail, (string?)null)
            .SetProperty(u => u.PasswordHash, (string?)null)
            .SetProperty(u => u.PhoneNumber, (string?)null)
            .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString())
            .SetProperty(u => u.LockoutEnabled, true)
            .SetProperty(u => u.LockoutEnd, DateTimeOffset.MaxValue)
            .SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return NoContent();
    }
}
