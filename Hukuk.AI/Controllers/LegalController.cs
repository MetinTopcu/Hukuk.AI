using Hukuk.AI.Legal;
using Microsoft.AspNetCore.Mvc;

namespace Hukuk.AI.Controllers;

// Girişsiz okunur: kayıt formu metni göstermeden önce çeker, kayıtta bu sürümü onaylar.
[ApiController]
[Route("api/legal")]
public class LegalController(PrivacyNotice privacy) : ControllerBase
{
    [HttpGet("privacy")]
    public PrivacyNotice Privacy() => privacy;
}
