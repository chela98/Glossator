# Glossator — Geliştirme Yol Haritası

Bu belge, **Glossator** platformunun tamamlanan ana sürümlerini (`v1.0` & `v2.0`) ve sıradaki mühendislik hedeflerini (`v2.1+`) şeffaf biçimde takip eder.

---

## Tamamlanan Sürümler

### ✅ v1.0 — Deterministik Çekirdek & Çoklu Ajan CLI Hattı
- [x] **Güvenli EPUB İşleme:** `SafeZipExtractor` (Zip-Slip & sıkıştırma limiti), `PathSanitizer`, `EpubManifestReader` & `EpubDocumentClassifier` (EPUB 2/3, Cover, Nav, Index, Auxiliary, Chapter ayrımı).
- [x] **AST Tabanlı Maskeleme & Kesitleme:** `HtmlSegmenter` (`<pre>` dışlama), `DomTreeWalkerMasker` (`⟦PH_xxxxxx⟧` koruma & `NormalizeTokens`), `HtmlSectionGrouper` (`1200–2800` token / maks `28` blok).
- [x] **3 Ajanlı Döngüsel İş Akışı (`Glossator.AI`):** `Microsoft.Agents.AI.Workflows` üzerinde `StyleAnalystExecutor` → `TranslatorExecutor` ⇄ `ReviewerExecutor` (maks. 3 deneme, C# ön filtresi, hedefli JSON Patch, tek seferlik JSON Self-Repair, post-patch C# Gatekeeper).
- [x] **SQLite Sözlük & Pedagojik `ProgressiveGloss`:** Gömülü **47 başlangıç terimi** (`seed_glossary.yaml`) + SQLite merkezi sözlük (`~/.glossator/glossator.db`); `ProgressiveGloss` **1. geçişte** hibrit `Türkçe (İngilizce)`, **2. geçiş ve sonrasında** yalın `Türkçe`.
- [x] **Single-Writer Checkpoint & Strict Paketleme:** `CheckpointStore` (`BoundedChannel(5000)` + SQLite WAL), `StrictXhtmlMarkupFormatter` ve atomik `EpubPackager`.
- [x] **CLI & Sözlük Yönetimi (`Glossator.Cli`):** `--concurrency`/`-j`, `--output`/`-o`, `--input`/`-i`, `--count`, `--fresh`, `--test`, `--extract-terms` ve `glossary` alt komutları (`list/add/edit/delete/search/export/import`).

### ✅ v2.0 — Glossator Studio (Desktop), Sağlayıcı Keşfi & Dayanıklılık
- [x] **Glossator Studio (Win11 Fluent UI / WPF):** 6 odak sayfası (`Kitaplığım`, `Aktif Kitap Masası`, `Terim Sözlüğü`, `Ayarlar`, `Telemetri`, `Hakkımda`), sürükle-bırak `.epub`, monogram kapak fallback, canlı alt günlük çekmecesi ve TitleBar anlık **Koyu / Aydınlık** tema geçişi (`StudioThemeService`).
- [x] **Tiered Çoklu Model & Akıl Yürütme (`ReasoningEffort`):** `StyleAnalyst`, `Translator` ve `Reviewer` için bağımsız sağlayıcı/model ataması ve 6 kademeli reasoning kontrolü (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`).
- [x] **Canlı Sağlayıcı Ping & Otomatik Model Keşfi (`ProviderDiscoveryService`):** `GET /models` (OpenAI, Ollama, Gemini) ile model çekme ve `/models` kapalı proxy'lerde `POST /chat/completions` probe fallback ile bağlantı/gecikme (`ms`) testi + `IsEditable` ComboBox arayüzü.
- [x] **Kısmi Bölüm İzolasyonu & Mikro Kurtarma:** `HtmlSegment.IsTranslationComplete`, `.part` / `.done` izolasyonu, Studio'dan tek tık kısmi sıfırlama ve `DirectTranslatorFallback` / `MicroRecovery`.
- [x] **Gözlemlenebilirlik, Canlı Kur & SSE Token Doğruluğu:** OpenTelemetry + `.NET Aspire Dashboard`, `ExchangeRateService` (Frankfurter ECB + ExchangeRate-API) ile canlı USD/TRY maliyet tahmini, `ResilienceChatClient` kümülatif SSE `UsageContent` + gizli reasoning token muhasebesi.
- [x] **Güvenli Kapanış & Kalite Kapıları:** Checkpoint/OTLP safe flush shutdown, `GLOSSATOR_SMOKE_TEST=1` duman testi, GitHub CI (`.github/workflows/ci.yml`) ve `42` test dosyasında **`458 / 458`** xUnit testi.

### ✅ v2.1 — Açık Kaynak Yayın Hijyeni, Sıfır-Yapılandırma & Otomatik Paketleme
- [x] **Kök `LICENSE` Dosyası & `.gitignore` Hijyeni:** Depo kök dizinine `MIT` [LICENSE](LICENSE) eklendi ve `.gitignore` içindeki `/roadmap.md` kuralı kaldırıldı.
- [x] **Sıfır-Yapılandırma İlk Açılış:** `App.xaml.cs` ve `Program.cs` yapılandırma zincirine `appsettings.example.json` temel şablon katmanı (`Evren` varsayılan + `Gemini`, `OpenAI`, yerel `Ollama`, `Custom`) ve `SettingsViewModel` hazır profil fallback'i eklendi.
- [x] **Çapraz Platform CI & Release Otomasyonu:** `.github/workflows/ci.yml` içine `ubuntu-latest` CLI (`net10.0`) derleme kontrolü; `.github/workflows/release.yml` içine `win-x64` Studio ZIP (`GLOSSATOR_SMOKE_TEST=1`), `win-x64`/`linux-x64` CLI paketleri, `SHA256SUMS.txt` ve SmartScreen bilgilendirmesi eklendi.

---

## Sıradaki Kilometre Taşları

### v2.2 — EPUB Yapısal Derinlik & Alan Profilleri
- [ ] **`nav.xhtml` & `toc.ncx` Başlık Yerelleştirmesi:** Yalnızca dosya referanslarını değil içindekiler başlık metinlerini de bölüm çevirisiyle otomatik senkronize etme.
- [ ] **Kenar Durum Tipografisi:** Görsel `alt` metinlerinin, dipnot (`footnote` / `aside epub:type="footnote"`) bloklarının ve yumuşak tireleme (soft hyphen) kırılmalarının özel işlenmesi.
- [ ] **Hazır Alan Profilleri:** Teknik yazılım yanında **Tıp**, **Hukuk** ve **Akademik Sosyal Bilimler** için hazır prompt ve sözlük başlangıç paketleri.

### v3.0 — Profesyonel CAT Yetenekleri & Sağlayıcı Yedekliliği
- [ ] **Bilingual (İki Dilli) Okuma Çıktısı:** Öğrenme amaçlı yan yana / paragraf altı İngilizce-Türkçe paralel EPUB çıktı modu.
- [ ] **TMX & Fuzzy Translation Memory:** Önceki kitaplardaki çeviri birimlerini (`.tmx`) dışa/içe aktarma ve yüksek benzerlikte cümle düzeyinde önbellek eşleştirme.
- [ ] **Otomatik Sağlayıcı Failover:** Birincil sağlayıcıda kalıcı `429` veya servis kesintisi yaşandığında kullanıcının tanımladığı yedek (secondary) sağlayıcıya otomatik devretme (circuit-breaker fallback).
