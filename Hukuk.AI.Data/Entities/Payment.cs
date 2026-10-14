namespace Hukuk.AI.Data.Entities;

public enum PaymentStatus
{
    Pending, // ödeme formu açıldı, sonuç bekleniyor
    Paid,    // sağlayıcı onayladı, kredi yüklendi
    Failed,
}

// Kredi paketi siparişi. Paket bilgisi (kredi, fiyat) sipariş anında kopyalanır: paketler sonradan değişse de
// eski siparişler ne satıldıysa onu gösterir.
public class Payment
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public AppUser User { get; set; } = null!;

    public string Package { get; set; } = "";

    public int Credits { get; set; }

    public decimal Price { get; set; }

    public string Currency { get; set; } = "";

    public PaymentStatus Status { get; set; }

    public string? ProviderToken { get; set; } // ödeme formu token'ı (callback bununla gelir)

    public string? ProviderPaymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }
}
