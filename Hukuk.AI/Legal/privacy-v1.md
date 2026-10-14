# Kişisel Verilerin İşlenmesine İlişkin Aydınlatma Metni

> Bu metin bir portföy projesi için hazırlanmış örnektir; hukuki danışmanlık niteliği taşımaz.

6698 sayılı Kişisel Verilerin Korunması Kanunu'nun (KVKK) 10. maddesi uyarınca, Hukuk.AI hizmetini kullanırken işlenen kişisel verileriniz hakkında sizi bilgilendiririz.

## 1. İşlenen veriler ve amaçları

| Veri | Amaç | Saklama süresi |
|---|---|---|
| E-posta adresi, parola özeti (hash) | Hesap oluşturma ve giriş | Hesap silinene kadar |
| Kredi hareketleri ve ödeme kayıtları | Kredi bakiyesinin yönetimi, mali kayıt yükümlülüğü | Hesap silinse de kişiyle bağlantısı kaldırılarak yasal süre boyunca |
| Sorduğunuz sorular | Cevap üretimi | Kişisel veri (TC kimlik no, telefon, e-posta, IBAN) içermeyen sorular ve cevapları, aynı soruyu soran diğer kullanıcılara hızlı cevap vermek için en fazla 7 gün önbellekte tutulur; kim tarafından sorulduğu tutulmaz. Kişisel veri içeren sorular önbelleğe alınmaz. |
| Yüklediğiniz belgeler ve sözleşme taslakları | Risk raporu, belge üzerinde soru-cevap, taslak üretimi | Son kullanımdan en fazla 2 saat sonra kendiliğinden silinir |
| IP adresi | İstek sınırlama (kötüye kullanımı önleme) | Kalıcı olarak saklanmaz |

Ödeme sırasında istenen ad, soyad ve adres yalnızca ödeme kuruluşuna (iyzico) iletilir, bizde saklanmaz. Kart bilgileriniz bize hiç ulaşmaz.

## 2. Aktarım

Sorularınız ve belgeleriniz, cevap üretimi için Microsoft Azure OpenAI ve Azure Document Intelligence hizmetlerine (yurt dışı, Fransa bölgesi) iletilir. Aracı (ajan) modunda soru, web araması için Tavily'ye iletilebilir. Ödeme bilgileri iyzico'ya iletilir.

## 3. Hukuki sebep

Verileriniz, sözleşmenin kurulması ve ifası (KVKK md. 5/2-c), hukuki yükümlülüklerin yerine getirilmesi (md. 5/2-ç) ve yurt dışına aktarım için açık rızanız (md. 9) hukuki sebeplerine dayanılarak işlenir.

## 4. Haklarınız (KVKK md. 11)

- Verilerinizin bir kopyasını almak için: `GET /api/account/export`
- Hesabınızı silmek için: `DELETE /api/account` (e-posta ve parolanız silinir, kredi ve ödeme kayıtlarınız kişiyle bağlantısı kaldırılarak saklanır)

Diğer talepleriniz için bizimle iletişime geçebilirsiniz.
