using System.ComponentModel.DataAnnotations;
using Hukuk.AI.Credits;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hukuk.AI.Controllers;

// Kredi paketi satın alma (iyzico checkout form). Akış: sipariş "pending" yazılır → kullanıcı iyzico sayfasında öder →
// iyzico tarayıcıyı callback'e yollar → sonuç iyzico'dan sorgulanır → sipariş "paid" + kredi tek transaction'da.
[Route("api/payments")]
public class PaymentsController(AppDbContext db, CreditService credits, CreditOptions options, IServiceProvider services,
    ILogger<PaymentsController> logger) : ApiController
{
    public record StartRequest([Required] string Package, [Required, StringLength(50)] string Name, [Required, StringLength(50)] string Surname,
        [Required, StringLength(200)] string Address, [Required, StringLength(50)] string City);

    public record StartResponse(Guid PaymentId, string PaymentPageUrl);

    public record PaymentResponse(Guid Id, string Package, int Credits, decimal Price, string Currency, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

    [HttpGet("packages")]
    [AllowAnonymous]
    public List<CreditPackage> Packages() => options.Packages;

    [HttpPost]
    public async Task<ActionResult<StartResponse>> Start(StartRequest request, CancellationToken cancellationToken)
    {
        if (services.GetService<IyzicoClient>() is not { } iyzico)
            return Problem("Ödeme şu anda kullanılamıyor.", statusCode: StatusCodes.Status503ServiceUnavailable);
        if (options.Packages.FirstOrDefault(p => p.Key == request.Package) is not { } package)
            return Problem($"Paketler: {string.Join(", ", options.Packages.Select(p => p.Key))}.", statusCode: StatusCodes.Status400BadRequest);

        // Silinmiş hesabın e-postası yok (anonimleştirildi); hâlâ geçerli access token'la ödeme başlatılamaz.
        if (await db.Users.Where(u => u.Id == UserId && u.DeletedAt == null).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken) is not { } email)
            return Unauthorized();
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Package = package.Key,
            Credits = package.Credits,
            Price = package.Price,
            Currency = options.Currency,
            Status = PaymentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        var checkout = await iyzico.StartAsync(payment, email, new BuyerInfo(request.Name, request.Surname, request.Address, request.City), ip);
        payment.ProviderToken = checkout.Token;
        await db.SaveChangesAsync(cancellationToken);
        return new StartResponse(payment.Id, checkout.PaymentPageUrl);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.UserId == UserId, cancellationToken) is { } p
            ? new PaymentResponse(p.Id, p.Package, p.Credits, p.Price, p.Currency, p.Status.ToString(), p.CreatedAt, p.PaidAt)
            : NotFound();

    // iyzico ödeme sayfası, işlem bitince kullanıcının tarayıcısını buraya POST ettirir (form alanı: token).
    // Girişsiz: tarayıcı yönlendirmesinde JWT yok. Güvenlik token'ın kendisinde değil, iyzico'ya geri sorulmasında.
    // Aynı callback iki kez gelse de kredi bir kez yüklenir: sipariş sadece "pending" iken "paid" olur (tek UPDATE),
    // ayrıca ledger'da payment_id unique.
    [HttpPost("callback")]
    [AllowAnonymous]
    [DisableRateLimiting]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<ActionResult<PaymentResponse>> Callback([FromForm] string token, CancellationToken cancellationToken)
    {
        if (services.GetService<IyzicoClient>() is not { } iyzico)
            return Problem("Ödeme şu anda kullanılamıyor.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.ProviderToken == token, cancellationToken);
        if (payment is null)
            return NotFound();

        if (payment.Status == PaymentStatus.Pending)
        {
            var providerPaymentId = await iyzico.GetPaidPaymentIdAsync(payment);
            var now = DateTimeOffset.UtcNow;
            if (providerPaymentId is null)
            {
                await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Failed), cancellationToken);
            }
            else
            {
                try
                {
                    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                    var updated = await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Paid)
                            .SetProperty(p => p.PaidAt, now)
                            .SetProperty(p => p.ProviderPaymentId, providerPaymentId), cancellationToken);
                    if (updated == 1)
                        await credits.AddPurchaseAsync(payment, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    logger.LogWarning("Ödeme {PaymentId} için kredi zaten yüklenmiş", payment.Id);
                }
            }
            payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id, cancellationToken);
        }
        return new PaymentResponse(payment.Id, payment.Package, payment.Credits, payment.Price, payment.Currency, payment.Status.ToString(), payment.CreatedAt, payment.PaidAt);
    }
}
