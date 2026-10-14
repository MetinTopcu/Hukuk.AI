namespace Hukuk.AI.Legal;

// KVKK aydınlatma metni. Metin Legal/privacy-v{Version}.md dosyasından okunur; metin değişirse yeni dosya + yeni sürüm,
// eski kullanıcıların hangi sürümü onayladığı users.privacy_version'da kalır.
public record PrivacyNotice(string Version, string Text)
{
    public static PrivacyNotice Load(string contentRoot, string version) =>
        new(version, File.ReadAllText(Path.Combine(contentRoot, "Legal", $"privacy-v{version}.md")));
}
