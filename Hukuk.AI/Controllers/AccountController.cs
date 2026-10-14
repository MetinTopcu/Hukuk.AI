using Hukuk.AI.Credits;
using Hukuk.AI.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hukuk.AI.Controllers;

// Giriş yapmış kullanıcının hesabı: bakiye, fiyatlar, kredi hareketleri.
[Route("api/account")]
public class AccountController(AppDbContext db, CreditOptions options) : ApiController
{
    public record AccountResponse(string Email, int Credits, Dictionary<string, int> Prices);

    public record TransactionResponse(long Id, string Type, int Amount, int BalanceAfter, string? Operation, DateTimeOffset CreatedAt);

    [HttpGet]
    public async Task<ActionResult<AccountResponse>> Get(CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == UserId).Select(u => new { u.Email, u.Credits }).SingleOrDefaultAsync(cancellationToken);
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
}
