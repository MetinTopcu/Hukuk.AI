namespace Hukuk.AI.Drafting;

// Taslak için kullanıcıdan gereken bilgi. Question: bilgi eksikse kullanıcıya sorulan soru.
// Required false: verilmezse sorulmaz, madde kanundaki kurala göre yazılır.
public record DraftField(string Key, string Label, string Question, bool Required = true);

// İskelet bölümü = taslakta bir madde. Articles: bölümün kanun dayanağı (Law içindeki madde numaraları, sabit liste).
// OnlyIfField: bölüm sadece bu bilgi verildiyse yazılır.
public record DraftSection(string Title, string Guidance, int[] Articles, string? OnlyIfField = null);

public record DraftTemplate(string Type, string Name, string Law, DraftField[] Fields, DraftSection[] Sections)
{
    public List<DraftField> Missing(IReadOnlyDictionary<string, string> fields) =>
        [.. Fields.Where(f => f.Required && !fields.ContainsKey(f.Key))];
}

// Taslak iskeletleri (kararlar 2026-10-05): kapsam kira + iş sözleşmesi (bilgi tabanının kapsadığı iki alan).
// Bölümün dayanağı arama ile değil sabit madde listesiyle gelir: doğrudan madde getirme eval'de 1.000, embedding yok,
// her taslakta aynı kaynaklar. Liste bilgi tabanındaki maddelerle sınırlı (TBK 299-378, İş Kanunu).
public static class DraftTemplates
{
    public const string SpecialRequests = "ozel_istekler";

    private static readonly DraftField SpecialRequestsField =
        new(SpecialRequests, "Özel istekler", "Sözleşmeye eklenmesini istediğiniz özel bir hüküm var mı?", Required: false);

    public static readonly DraftTemplate Kira = new("kira", "Kira Sözleşmesi", "6098",
    [
        new("kiraya_veren", "Kiraya veren", "Kiraya verenin adı soyadı (ya da unvanı) nedir?"),
        new("kiraci", "Kiracı", "Kiracının adı soyadı (ya da unvanı) nedir?"),
        new("kiralanan_adres", "Kiralananın adresi", "Kiralanan yerin adresi nedir?"),
        new("kullanim_amaci", "Kullanım amacı", "Kiralanan konut olarak mı, işyeri olarak mı kullanılacak?"),
        new("baslangic_tarihi", "Başlangıç tarihi", "Kira hangi tarihte başlayacak?"),
        new("sure", "Kira süresi", "Kira süresi ne kadar (örn. 1 yıl)?"),
        new("kira_bedeli", "Aylık kira bedeli", "Aylık kira bedeli ne kadar?"),
        new("depozito", "Güvence bedeli (depozito)", "Depozito alınacak mı, alınacaksa ne kadar?"),
        new("odeme", "Ödeme günü ve şekli", "Kira hangi gün ve nasıl (banka hesabı, elden) ödenecek?", Required: false),
        new("kira_artisi", "Kira artışı", "Kira bedeli yenileme dönemlerinde nasıl artacak?", Required: false),
        new("yan_giderler", "Yan giderler", "Aidat, elektrik, su gibi giderleri kim ödeyecek?", Required: false),
        SpecialRequestsField,
    ],
    [
        new("Taraflar", "Kiraya veren ve kiracının kimlik ve adres bilgileri.", []),
        new("Kiralanan ve Kullanım Amacı", "Kiralananın adresi, niteliği ve hangi amaçla kullanılacağı.", [299, 339]),
        new("Kira Süresi", "Başlangıç tarihi, süre ve süre sonunda sözleşmenin durumu.", [300, 327, 347]),
        new("Kira Bedeli ve Ödeme", "Aylık kira bedeli, ödeme günü ve ödeme şekli.", [313, 314]),
        new("Kira Bedelinin Artışı", "Yenilenen kira dönemlerinde kira bedelinin nasıl belirleneceği.", [343, 344]),
        new("Güvence Bedeli", "Depozitonun tutarı, nerede tutulacağı ve iadesi. Depozito alınmayacaksa bunu belirt.", [342]),
        new("Yan Giderler ve Vergiler", "Kullanım giderleri, aidat, vergi ve benzeri yükümlülüklerin kime ait olduğu.", [302, 303, 341]),
        new("Kiralananın Teslimi ve Ayıplar", "Kiralananın teslimi ve ayıplardan sorumluluk.", [301, 304, 305]),
        new("Kullanım, Bakım ve Onarım", "Özenle kullanma, komşulara saygı, temizlik ve bakım, ayıpların bildirilmesi.", [316, 317, 318, 319]),
        new("Yenilik ve Değişiklikler", "Kiralananda tarafların yapabileceği yenilik ve değişiklikler.", [320, 321]),
        new("Alt Kira ve Devir", "Kiralananın başkasına kiraya verilmesi ve kira ilişkisinin devri.", [322, 323]),
        new("Kiracının Temerrüdü", "Kira bedelinin ödenmemesinin sonuçları.", [315, 346]),
        new("Sözleşmenin Sona Ermesi", "Tarafların sözleşmeyi hangi hallerde ve nasıl sona erdirebileceği.", [347, 348, 350, 352]),
        new("Kiralananın Geri Verilmesi", "Sözleşme sona erince kiralananın geri verilmesi ve gözden geçirilmesi.", [334, 335]),
        new("Özel Hükümler", "Sadece kullanıcının özel istekleri.", [346], OnlyIfField: SpecialRequests),
        new("Yürürlük", "Sözleşmenin kaç nüsha düzenlendiği ve imza tarihi.", []),
    ]);

    public static readonly DraftTemplate Is = new("is", "İş Sözleşmesi", "4857",
    [
        new("isveren", "İşveren", "İşverenin adı soyadı (ya da unvanı) nedir?"),
        new("isci", "İşçi", "İşçinin adı soyadı nedir?"),
        new("gorev", "Görev", "İşçi hangi görevde (unvan, iş tanımı) çalışacak?"),
        new("isyeri", "İşyeri", "İşçinin çalışacağı işyerinin adresi nedir?"),
        new("baslangic_tarihi", "Başlangıç tarihi", "İşçi hangi tarihte işe başlayacak?"),
        new("sure_turu", "Sözleşme süresi", "Sözleşme belirsiz süreli mi, belirli süreli mi (belirli ise bitiş tarihi ve nedeni)?"),
        new("ucret", "Ücret", "Aylık ücret ne kadar (brüt mü, net mi)?"),
        new("calisma_suresi", "Çalışma süresi", "Haftalık çalışma süresi ve çalışma günleri nedir?", Required: false),
        new("deneme_suresi", "Deneme süresi", "Deneme süresi olacak mı, olacaksa ne kadar?", Required: false),
        new("yan_haklar", "Yan haklar", "Yemek, yol, prim gibi yan haklar var mı?", Required: false),
        SpecialRequestsField,
    ],
    [
        new("Taraflar", "İşveren ve işçinin kimlik ve adres bilgileri.", []),
        new("İşin Tanımı ve İşyeri", "İşçinin görevi, işin yapılacağı yer ve çalışma koşullarında değişiklik.", [8, 22]),
        new("Sözleşmenin Süresi", "İşe başlama tarihi; sözleşmenin belirsiz ya da belirli süreli olduğu.", [11, 12]),
        new("Deneme Süresi", "Deneme süresi ve bu sürede fesih. Deneme süresi istenmiyorsa olmadığını belirt.", [15]),
        new("Çalışma Süresi ve Ara Dinlenmesi", "Haftalık çalışma süresi, günlere dağılımı ve ara dinlenmesi.", [63, 68]),
        new("Fazla Çalışma", "Fazla çalışmanın koşulları ve karşılığı.", [41]),
        new("Ücret", "Ücretin tutarı, ödeme zamanı ve şekli, varsa yan haklar.", [32, 34, 37]),
        new("Hafta Tatili ve Genel Tatiller", "Hafta tatili ile ulusal bayram ve genel tatil günleri.", [46, 47]),
        new("Yıllık Ücretli İzin", "Yıllık izin süresi, kullanımı ve izin ücreti.", [53, 56, 57]),
        new("Tarafların Genel Yükümlülükleri", "İşçinin işi özenle yapması, işverenin işçiyi gözetmesi; genel ve dengeli, ceza koşulu yok.", []),
        new("Sözleşmenin Feshi", "Bildirim süreleriyle fesih, geçerli sebep ve haklı nedenle derhal fesih.", [17, 18, 24, 25]),
        new("Özel Hükümler", "Sadece kullanıcının özel istekleri.", [], OnlyIfField: SpecialRequests),
        new("Yürürlük", "Sözleşmenin kaç nüsha düzenlendiği ve imza tarihi.", []),
    ]);

    public static readonly DraftTemplate[] All = [Kira, Is];

    public static DraftTemplate? Find(string type) => All.FirstOrDefault(t => t.Type == type);
}
