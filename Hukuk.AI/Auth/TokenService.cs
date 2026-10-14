using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hukuk.AI.Auth;

// Key user secrets'ta (Auth:JwtKey); HS256 için en az 32 bayt.
public record JwtOptions(string Issuer, string Audience, string Key, int AccessTokenMinutes, int RefreshTokenDays)
{
    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

// ExpiresIn: access token'ın saniye cinsinden ömrü.
public record TokenPair(string AccessToken, int ExpiresIn, string RefreshToken);

// Kararlar (2026-10-13): kısa ömürlü JWT access token (15 dk; sunucuda saklanmaz, iptal edilemez) + Postgres'te
// hash'i tutulan refresh token (7 gün). Refresh token tek kullanımlık: her yenilemede eskisi iptal edilip yenisi verilir.
public class TokenService(AppDbContext db, JwtOptions options)
{
    // Yeni giriş: yeni token ailesi.
    public Task<TokenPair> IssueAsync(AppUser user, CancellationToken cancellationToken = default) =>
        IssueAsync(user, Guid.NewGuid(), cancellationToken);

    // Geçersiz, süresi dolmuş ya da daha önce kullanılmış token'da null.
    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.AsNoTracking().Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
            return null;

        // İptali "hâlâ geçerliyse" koşuluyla tek sorguda yap: aynı token'la aynı anda gelen iki istekten sadece biri
        // 1 satır günceller. 0 satır = token zaten kullanılmış → çalınmış olabilir, ailenin tamamını iptal et
        // (hem hırsızın hem gerçek kullanıcının oturumu düşer, kullanıcı yeniden giriş yapar).
        var now = DateTimeOffset.UtcNow;
        var revoked = await db.RefreshTokens.Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        if (revoked == 0)
        {
            await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
            return null;
        }

        return token.ExpiresAt <= now ? null : await IssueAsync(token.User, token.FamilyId, cancellationToken);
    }

    // Çıkış: token'ın ailesi iptal edilir. Access token süresi dolana kadar (en fazla 15 dk) geçerli kalır.
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (familyId is not null)
            await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    private async Task<TokenPair> IssueAsync(AppUser user, Guid familyId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var accessToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(JwtRegisteredClaimNames.Email, user.Email!)]),
            Expires = now.AddMinutes(options.AccessTokenMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256),
        });

        var refreshToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); // 256 bit rastgele
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = Hash(refreshToken),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.RefreshTokenDays),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new TokenPair(accessToken, options.AccessTokenMinutes * 60, refreshToken);
    }

    // Token zaten 256 bit rastgele olduğu için tuzsuz SHA-256 yeterli (parola değil; sözlük saldırısı anlamsız).
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
