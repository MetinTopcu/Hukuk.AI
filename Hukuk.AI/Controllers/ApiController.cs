using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Hukuk.AI.Controllers;

// Giriş gerektiren uçların ortak tabanı: JWT zorunlu, kullanıcı başına istek sınırı.
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.User)]
public abstract class ApiController : ControllerBase
{
    // Token'ı biz ürettik ve imzası doğrulandı: "sub" her zaman kullanıcı id'si.
    protected Guid UserId => Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

    protected ObjectResult InsufficientCredits() =>
        Problem("Yetersiz kredi.", statusCode: StatusCodes.Status402PaymentRequired);
}

public static class RateLimitPolicies
{
    public const string User = "user"; // kullanıcı başına
    public const string Auth = "auth"; // IP başına: kayıt/giriş (parola denemelerini yavaşlatır)
}
