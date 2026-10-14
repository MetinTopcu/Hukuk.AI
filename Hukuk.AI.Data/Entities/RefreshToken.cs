namespace Hukuk.AI.Data.Entities;

// Refresh token'ın kendisi saklanmaz, sadece SHA-256 hash'i (veritabanı sızarsa token'lar kullanılamaz).
// Rotasyon: her yenilemede eski satır iptal edilir (RevokedAt), aynı FamilyId ile yenisi yazılır.
// İptal edilmiş bir token tekrar gelirse (çalınmış olabilir) ailenin tamamı iptal edilir.
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public AppUser User { get; set; } = null!;

    public Guid FamilyId { get; set; } // tek girişten türeyen token zinciri

    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
