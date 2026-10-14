using System.Globalization;
using Hukuk.AI.Data.Entities;
using Iyzipay;
using Iyzipay.Model;
using Iyzipay.Request;
using Payment = Hukuk.AI.Data.Entities.Payment;

namespace Hukuk.AI.Payments;

// BaseUrl: sandbox https://sandbox-api.iyzipay.com, canlı https://api.iyzipay.com. CallbackBaseUrl: bu API'nin dışarıdan
// görünen adresi (ödeme sonrası iyzico kullanıcının tarayıcısını buraya POST ettirir; localhost da çalışır).
public record IyzicoOptions(string ApiKey, string SecretKey, string BaseUrl, string CallbackBaseUrl);

// iyzico'nun zorunlu tuttuğu alıcı bilgisi. Saklanmaz, sadece ödeme isteğinde iyzico'ya iletilir.
public record BuyerInfo(string Name, string Surname, string Address, string City);

public record CheckoutStart(string Token, string PaymentPageUrl);

// iyzico "checkout form" akışı: ödeme sayfası iyzico'da açılır, kart bilgisi bu API'ye hiç gelmez (PCI kapsamı dışı).
public class IyzicoClient(IyzicoOptions options)
{
    // TC kimlik no toplanmıyor (KVKK: gereksiz veri); iyzico'nun bu durum için önerdiği yer tutucu.
    private const string PlaceholderIdentityNumber = "11111111111";

    private Options ApiOptions => new() { ApiKey = options.ApiKey, SecretKey = options.SecretKey, BaseUrl = options.BaseUrl };

    public async Task<CheckoutStart> StartAsync(Payment payment, string email, BuyerInfo buyer, string ip)
    {
        var price = payment.Price.ToString("0.00", CultureInfo.InvariantCulture);
        var address = new Address
        {
            ContactName = $"{buyer.Name} {buyer.Surname}",
            City = buyer.City,
            Country = "Turkey",
            Description = buyer.Address,
        };
        var request = new CreateCheckoutFormInitializeRequest
        {
            Locale = Locale.TR.ToString(),
            ConversationId = payment.Id.ToString("N"),
            Price = price,
            PaidPrice = price,
            Currency = payment.Currency,
            BasketId = payment.Id.ToString("N"),
            PaymentGroup = PaymentGroup.PRODUCT.ToString(),
            CallbackUrl = $"{options.CallbackBaseUrl.TrimEnd('/')}/api/payments/callback",
            EnabledInstallments = [1],
            Buyer = new Buyer
            {
                Id = payment.UserId.ToString("N"),
                Name = buyer.Name,
                Surname = buyer.Surname,
                Email = email,
                IdentityNumber = PlaceholderIdentityNumber,
                RegistrationAddress = buyer.Address,
                City = buyer.City,
                Country = "Turkey",
                Ip = ip,
            },
            BillingAddress = address,
            ShippingAddress = address,
            BasketItems =
            [
                new BasketItem
                {
                    Id = payment.Package,
                    Name = $"{payment.Credits} kredi",
                    Category1 = "Kredi",
                    ItemType = BasketItemType.VIRTUAL.ToString(),
                    Price = price,
                },
            ],
        };

        var result = await CheckoutFormInitialize.Create(request, ApiOptions);
        if (result.Status != "success")
            throw new InvalidOperationException($"iyzico ödeme formu açılamadı: {result.ErrorCode} {result.ErrorMessage}");
        return new CheckoutStart(result.Token, result.PaymentPageUrl);
    }

    // Callback'e gelen bilgiye güvenilmez (herkes POST edebilir): ödemenin sonucu token ile iyzico'dan sorulur.
    // Sadece ödeme başarılıysa, bu siparişe aitse ve tutar tutuyorsa sağlayıcının ödeme numarası döner; aksi halde null.
    public async Task<string?> GetPaidPaymentIdAsync(Payment payment)
    {
        var result = await CheckoutForm.Retrieve(new RetrieveCheckoutFormRequest { Token = payment.ProviderToken }, ApiOptions);
        var paid = result.Status == "success"
            && result.PaymentStatus == "SUCCESS"
            && result.BasketId == payment.Id.ToString("N")
            && decimal.TryParse(result.PaidPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var paidPrice)
            && paidPrice == payment.Price;
        return paid ? result.PaymentId : null;
    }
}
