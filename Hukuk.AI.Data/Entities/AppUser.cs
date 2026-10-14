using Microsoft.AspNetCore.Identity;

namespace Hukuk.AI.Data.Entities;

// Identity kullanıcısı + kredi bakiyesi. Credits hızlı okuma için; asıl kayıt CreditTransaction satırları (ledger).
// Bakiye sadece CreditService üzerinden değişir (atomik UPDATE + aynı transaction'da ledger satırı).
public class AppUser : IdentityUser<Guid>
{
    public int Credits { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
