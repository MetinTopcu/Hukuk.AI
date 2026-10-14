using System.ComponentModel.DataAnnotations;
using Hukuk.AI.Auth;
using Hukuk.AI.Credits;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Legal;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Hukuk.AI.Controllers;

// Kayıt, giriş, token yenileme, çıkış. Parola hash'i ve kilitleme ASP.NET Core Identity'de (UserManager).
[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public class AuthController(UserManager<AppUser> users, TokenService tokens, CreditService credits, AppDbContext db,
    PrivacyNotice privacy) : ControllerBase
{
    public record CredentialsRequest([Required, EmailAddress, StringLength(256)] string Email, [Required, StringLength(100)] string Password);

    // AcceptPrivacy + PrivacyVersion: kullanıcı GET api/legal/privacy'deki metni bu sürümüyle onayladı (KVKK).
    // Sürüm eşleşmezse metin bu arada değişmiş demektir; istemci yeni metni göstermeli.
    public record RegisterRequest([Required, EmailAddress, StringLength(256)] string Email, [Required, StringLength(100)] string Password,
        bool AcceptPrivacy, [Required, StringLength(20)] string PrivacyVersion);

    public record RefreshRequest([Required, StringLength(200)] string RefreshToken);

    // Kullanıcı ve kayıt hediyesi tek transaction'da: kredisiz kullanıcı ya da kullanıcısız hareket kalmaz.
    [HttpPost("register")]
    public async Task<ActionResult<TokenPair>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (!request.AcceptPrivacy || request.PrivacyVersion != privacy.Version)
        {
            ModelState.AddModelError(nameof(request.AcceptPrivacy), $"Aydınlatma metninin güncel sürümü ({privacy.Version}) onaylanmalı.");
            return ValidationProblem();
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(), UserName = request.Email, Email = request.Email, CreatedAt = now,
            PrivacyVersion = privacy.Version, PrivacyAcceptedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors)
                ModelState.AddModelError(error.Code.Contains("Password") ? nameof(request.Password) : nameof(request.Email), error.Description);
            return ValidationProblem();
        }
        await credits.GrantSignupGiftAsync(user.Id, cancellationToken);
        var pair = await tokens.IssueAsync(user, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return pair;
    }

    // Yanlış e-posta ile yanlış parola aynı cevabı verir (hangi e-postaların kayıtlı olduğu sızmasın).
    [HttpPost("login")]
    public async Task<ActionResult<TokenPair>> Login(CredentialsRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || await users.IsLockedOutAsync(user))
            return Invalid();

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user); // üst üste hatalı denemede hesap geçici kilitlenir (Identity lockout)
            return Invalid();
        }
        await users.ResetAccessFailedCountAsync(user);
        return await tokens.IssueAsync(user, cancellationToken);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<TokenPair>> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        await tokens.RefreshAsync(request.RefreshToken, cancellationToken) is { } pair
            ? pair
            : Problem("Oturum geçersiz, yeniden giriş yapın.", statusCode: StatusCodes.Status401Unauthorized);

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        await tokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    private ObjectResult Invalid() => Problem("E-posta veya parola hatalı.", statusCode: StatusCodes.Status401Unauthorized);
}
