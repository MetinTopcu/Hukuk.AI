using System.Text.RegularExpressions;

namespace Hukuk.AI.Retrieval;

// KVKK: kişisel veri içeren soru önbelleğe (semantic cache + embedding cache) girmez ve oradan okunmaz.
// Semantic cache kullanıcılar arasında paylaşımlı: başkasının TC no'su ya da telefonu benzer soru soran birine
// cevapla birlikte dönebilirdi. Kararlar (2026-10-14): maskeleme değil atlama; sadece yapısal veriler (TC kimlik no,
// telefon, e-posta, IBAN). İsimler/adresler yakalanmaz (regex ile güvenilir değil).
public static partial class PersonalData
{
    public static bool Contains(string text) =>
        Email().IsMatch(text) || Iban().IsMatch(text) || Phone().IsMatch(text)
        || TcCandidate().Matches(text).Any(m => IsValidTcNo(m.Value));

    // TC kimlik no: 11 hane, ilk hane 0 değil, son iki hane kontrol hanesi. Kontrol, rastgele 11 haneli sayıları
    // (ör. tutarlar) kişisel veri saymamak için.
    private static bool IsValidTcNo(string s)
    {
        var d = s.Select(c => c - '0').ToArray();
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var d10 = ((odd * 7 - even) % 10 + 10) % 10;
        var d11 = d.Take(10).Sum() % 10;
        return d[9] == d10 && d[10] == d11;
    }

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)")]
    private static partial Regex TcCandidate();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();

    // TR + 2 kontrol hanesi + 22 hane, araya boşluk girebilir.
    [GeneratedRegex(@"\bTR\s?\d{2}(\s?\d){22}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Iban();

    // Başında +90 / 0 olan 10 haneli numara ya da 5xx ile başlayan cep numarası; boşluk, tire, parantez olabilir.
    [GeneratedRegex(@"(?<!\d)((\+90|0)[\s-]?\(?[2-5]\d{2}\)?|\(?5\d{2}\)?)[\s-]?\d{3}[\s-]?\d{2}[\s-]?\d{2}(?!\d)")]
    private static partial Regex Phone();
}
