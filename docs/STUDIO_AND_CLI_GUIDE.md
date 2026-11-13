# Glossator Studio (Masaüstü) ve CLI Kullanım Kılavuzu

Glossator projesinde iki çalıştırma yüzeyi bulunur: günlük görsel kullanım için tasarlanan **Glossator Studio (`Glossator.App`)** ve otomasyon/platform bağımsız terminal işleri için **Glossator CLI (`Glossator.Cli`)**.

---

## 1. Glossator Studio (Windows 11 Fluent Masaüstü)

Çözümü Windows üzerinde çalıştırmak için:

```powershell
dotnet run --project Glossator.App/Glossator.App.csproj
```

Studio penceresi ([ShellWindow.xaml](../Glossator.App/Views/ShellWindow.xaml)) sol kenar çubuğundan 6 odak sayfası, **Son Kitaplar** hızlı geçiş listesi, sağ üst başlıkta **Koyu / Aydınlık** tema anahtarı ve pencere altına sabitlenmiş VS Code tarzı katlanabilir **Çıktı & Günlük Çekmecesi** sunar. İstediğiniz `.epub` dosyasını doğrudan pencerenin üzerine sürükleyip bırakabilirsiniz.

### Sayfalar ve Yetenekler

#### 1) Kitaplığım (`LibraryView`)
* Son açılan EPUB dosyalarını veya açık EPUB klasörlerini kapak görseliyle (EPUB 3 `cover-image` / EPUB 2 `meta cover`, kapak yoksa monogram fallback) 3:4 kartlar halinde listeler.
* Başlık, yazar veya yayıncıya göre anlık arama yapar; diskten taşınmış/silinmiş eski kitap kayıtlarını tek tıkla temizler.

#### 2) Aktif Kitap Masası (`BookWorkspaceView`)
* Kitap meta verilerini, toplam ilerleme yüzdesini ve her bölümün (`Chapter`, `Auxiliary`, `Cover`, `Navigation`, `Index`) durumunu gösterir:
  * **Tamamlandı:** `.done` işaretli tam çeviri (nihai EPUB paketine dahil edilir).
  * **Yarım (`.part`):** Kısmi, test modu veya eksik blok kalmış çeviri. Tek tıkla bölümün checkpoint'ini sıfırlayabilir ya da kalan bloklardan devam ederek tamamlayabilirsiniz.
  * **Çevrilmemiş:** Henüz işlenmemiş bölüm.
* **Filtre & Toplu Seçim:** `Tümü`, `Çevrilmemiş`, `Tamamlanan`, `Sadece Bölümler` filtreleri; arama kutusu; aralık seçici (örn. `1-5, 8`).
* **Motor Ayarları Popover:**
  * Eşzamanlı işçi sayısı (`WorkersCount`: `1..8`).
  * `Sıfırdan Çevir (--fresh)`: Seçili bölümlerin SQLite checkpoint ve eski `.done`/`.part` dosyalarını temizler.
  * `Test Modu`: Her bölümün yalnızca ilk 2 kesitini işler ve sonucu `.part` olarak izole eder.
  * `Kesit Limiti`: Bölüm başına işlenecek özel kesit sayısı (örn. `3`).
* **Çıktı Paketleme:** **EPUB Paketle** komutuyla tam çevrilmiş bölümleri + güncellenmiş iç linkleri/TOC'u atomik olarak `*_TR.epub` dosyasına paketler.

#### 3) Terim Sözlüğü (`GlossaryView`)
* Merkezi SQLite sözlüğünü (`~/.glossator/glossator.db`) anlık arama, politika filtresi (`Tümü`, `AlwaysEnglish`, `AlwaysTurkish`, `ProgressiveGloss`) ve yalnızca onaysızlar filtresiyle yönetir.
* Satır içi düzenleme ve onay durumu değiştirme, bağlamsal şerit üzerinden toplu onay/politika güncelleme/silme, hatalı kayıt temizliği.
* **Bu Kitaptan Terim Tara:** Açık EPUB içindeki dizin/terim ipuçlarını tarayıp ana onaylı kayıtları ezmeden içe aktarır.
* **YAML / JSON Dışa & İçe Aktarım:** Takım içi paylaşım ve Git yedeklemesi için çift yönlü senkronizasyon.

#### 4) Ayarlar (`SettingsView`)
* **Sağlayıcılar & Modeller Alt Sekmesi:**
  * Aktif sağlayıcı seçimi, yeni profil ekleme/silme, Endpoint / API Key / Varsayılan Model düzenleme.
  * **Ping (Bağlantı Testi):** Uç nokta erişilebilirliğini, API anahtarı yetkisini ve gecikmeyi (`ms`) ölçer; `/models` kapalıysa `Varsayılan Model` ile `/chat/completions` probe fallback yapar.
  * **Modelleri Çek:** Sağlayıcıdaki modelleri çekerek hem Varsayılan Model hem Ajan **Özel Model ID** düzenlenebilir açılır kutularına yükler.
  * **Ajan Rol Atamaları:** `StyleAnalyst`, `Translator` ve `Reviewer` için bağımsız `Sağlayıcı`, `Özel Model ID` ve `Akıl Yürütme (Reasoning: none, minimal, low, medium, high, xhigh)` seçimi.
* **Prompt Editörü Alt Sekmesi:**
  * `StyleAnalystPrompt.md`, `TranslatorPrompt.md`, `ReviewerPrompt.md` dosyalarını uygulama içinden düzenleyip `Prompts/` klasörüne kaydetme veya fabrika Gömülü Kaynağına sıfırlama.

#### 5) Telemetri (`TelemetryView`)
* **Canlı KPI Kartları:** Aktif İşçi, Çevrilen Kesit, Tamamlanan Bölüm, Hız (`tok/s` & `TTFT`), Yama/Red/Kurtarma, Canlı Akış Tokenları.
* **Aspire OpenTelemetry Durumu:** OTLP gRPC (`localhost:4317`) portuna TCP sağlık kontrolü, Aspire Dashboard (`localhost:18888`) kısayolu ve OTLP Endpoint / Servis Adı kaydı.
* **Canlı USD / TRY Kur Yönetimi:** `ExchangeRateService` üzerinden Frankfurter (ECB) & ExchangeRate-API'den kur yenileme veya manuel sabit kur kaydı.

#### 6) Hakkımda (`AboutView`)
* Geliştirici bilgileri, teknik mimari rozetleri ve 3 aşamalı boru hattı özeti.

---

## 2. Docker ile .NET Aspire Gözlemlenebilirlik Paneli

Canlı OpenTelemetry log, trace waterfall ve token metriklerini görmek için Docker üzerinde Aspire Dashboard başlatabilirsiniz:

```powershell
docker run --rm -it -d -p 18888:18888 -p 4317:18889 --name aspire-dashboard mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Ardından tarayıcıdan `http://localhost:18888` adresini açın. Dashboard kapalıyken Glossator Studio **bekletmeden ve donmadan** çalışmaya devam eder.

---

## 3. Glossator CLI Kullanımı (Headless / Linux & macOS Uyumlu)

```powershell
# Çeviri hattını başlat (varsayılan concurrency: 3)
dotnet run --project Glossator.Cli -- "C:\Kitaplar\kitap.epub" -o "kitap_TR.epub" -j 3

# Desteklenen bayraklar
#   -o, --output <yol>        Hedef EPUB yolu
#   -i, --input <yol>         Girdi EPUB veya açık klasör
#   -j, --concurrency <N>     Paralel işçi sayısı (1 .. 2*CPU)
#   --count <N>               Bölüm başına yalnızca ilk N kesiti işle (.part yazar)
#   --fresh                   Checkpoint ve önceki çıktıları temizleyerek sıfırdan çevir
#   --test                    Test modu
#   --extract-terms           Kitaptan terim çıkarıp karantina (glossary.extracted.yaml) üret

# Sözlük CLI alt komutları
dotnet run --project Glossator.Cli -- glossary list --policy ProgressiveGloss --search "agent"
dotnet run --project Glossator.Cli -- glossary add -e "zero-shot learning" -t "sıfır örnekli öğrenme" -p ProgressiveGloss
dotnet run --project Glossator.Cli -- glossary edit -e "pipeline" -t "akış hattı" -p AlwaysTurkish
dotnet run --project Glossator.Cli -- glossary delete "zero-shot learning"
dotnet run --project Glossator.Cli -- glossary search "token"
dotnet run --project Glossator.Cli -- glossary export master_glossary.yaml -f yaml
dotnet run --project Glossator.Cli -- glossary import ./glossary.extracted.yaml
```

---

## 4. Calibre ile PDF ↔ EPUB İş Akışı

Glossator, W3C EPUB 3 XHTML bütünlüğünü korumak için doğrudan `.epub` işler. PDF kaynaklı veya PDF çıktısı gereken senaryolarda **Calibre (`ebook-convert`)** ile kayıpsız dönüşüm yapabilirsiniz:

```powershell
# Çevrilen Türkçe EPUB'ı A4 sayfa numaralı PDF'e dönüştürme
ebook-convert "Kitap_TR.epub" "Kitap_TR.pdf" --paper-size a4 --pdf-page-numbers --margin-left 36 --margin-right 36
```

PDF'ten başlamanız gerekiyorsa önce Calibre üzerinden EPUB'a dönüştürüp Glossator ile Türkçeye yerelleştirebilir, ardından yeniden PDF alabilirsiniz.
