namespace Hukuk.AI.Data.Entities;

public enum CreditTransactionType
{
    Signup,   // kayıt hediyesi (+)
    Usage,    // işlem ücreti (-), istek başında düşülür
    Refund,   // başarısız işlemin iadesi (+)
    Purchase, // ödeme ile yükleme (+)
}

// Kredi hareketi (ledger): satırlar değişmez ve silinmez, bakiye bu satırların toplamıdır.
public class CreditTransaction
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public AppUser User { get; set; } = null!;

    public CreditTransactionType Type { get; set; }

    public int Amount { get; set; } // işaretli: düşüm negatif

    public int BalanceAfter { get; set; }

    public string? Operation { get; set; } // Usage/Refund için: "question", "agent", "document", ...

    public long? RefundOfId { get; set; } // Refund için: iade edilen Usage satırı (unique: aynı işlem iki kez iade edilemez)

    public Guid? PaymentId { get; set; } // Purchase için (unique: aynı ödeme iki kez kredi yükleyemez)

    public DateTimeOffset CreatedAt { get; set; }
}
