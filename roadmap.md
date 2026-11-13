# Glossator — Geliştirme Yol Haritası

Bu belge, **Glossator** platformunun tamamlanan sürümlerini (`v1.0` → `v1.3.0-beta.1`) ve sıradaki mühendislik hedeflerini (`v1.3.0+`) şeffaf biçimde takip eder.

---

## Tamamlanan Sürümler

### ✅ v1.0 — Deterministik Çekirdek & Çoklu Ajan CLI Hattı
- [x] **Güvenli EPUB İşleme:** `SafeZipExtractor` (Zip-Slip & sıkıştırma limiti), `PathSanitizer`, `EpubManifestReader` & `EpubDocumentClassifier` (EPUB 2/3, Cover, Nav, Index, Auxiliary, Chapter ayrımı).
- [x] **AST Tabanlı Maskeleme & Kesitleme:** `HtmlSegmenter` (`<pre>` dışlama), `DomTreeWalkerMasker` (`⟦PH_xxxxxx⟧` koruma & `NormalizeTokens`), `HtmlSectionGrouper` (`1200–2800` token / maks `28` blok).
- [x] **3 Ajanlı Döngüsel İş Akışı (`Glossator.AI`):** `Microsoft.Agents.AI.Workflows` üzerinde `StyleAnalystExecutor` → `TranslatorExecutor` ⇄ `ReviewerExecutor` (maks. 3 deneme, C# ön filtresi, hedefli JSON Patch, tek seferlik JSON Self-Repair, post-patch C# Gatekeeper).
- [x] **SQLite Sözlük & Pedagojik `ProgressiveGloss`:** Gömülü **47 başlangıç terimi** (`seed_glossary.yaml`) + SQLite merkezi sözlük (`~/.glossator/glossator.db`); `ProgressiveGloss` **1. geçişte** hibrit `Türkçe (İngilizce)`, **2. geçiş ve sonrasında** yalın `Türkçe`.
- [x] **Single-Writer Checkpoint & Strict Paketleme:** `CheckpointStore` (`BoundedChannel(5000)` + SQLite WAL), `StrictXhtmlMarkupFormatter` ve atomik `EpubPackager`.
- [x] **CLI & Sözlük Yönetimi (`Glossator.Cli`):** `--concurrency`/`-j`, `--output`/`-o`, `--input`/`-i`, `--count`, `--fresh`, `--test`, `--extract-terms` ve `glossary` alt komutları (`list/add/edit/delete/search/export/import`).

### 🧪 v1.3.0-beta.1 (Mevcut Açık Beta) — Glossator Studio, Sağlayıcı Keşfi & .NET 10 Sertleştirme
- [x] **Glossator Studio (Win11 Fluent UI / WPF):** 6 odak sayfası (`Kitaplığım`, `Aktif Kitap Masası`, `Terim Sözlüğü`, `Ayarlar`, `Telemetri`, `Hakkımda`), sürükle-bırak `.epub`, monogram kapak fallback, canlı alt günlük çekmecesi ve TitleBar anlık **Koyu / Aydınlık** tema geçişi (`StudioThemeService`).
- [x] **Tiered Çoklu Model & Akıl Yürütme (`ReasoningEffort`):** `StyleAnalyst`, `Translator` ve `Reviewer` için bağımsız sağlayıcı/model ataması ve 6 kademeli reasoning kontrolü (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`).
- [x] **Canlı Sağlayıcı Ping & Otomatik Model Keşfi (`ProviderDiscoveryService`):** `GET /models` (OpenAI, Ollama, Gemini) ile model çekme ve `/models` kapalı proxy'lerde `POST /chat/completions` probe fallback ile bağlantı/gecikme (`ms`) testi + `IsEditable` ComboBox arayüzü.
- [x] **Kısmi Bölüm İzolasyonu & Mikro Kurtarma:** `HtmlSegment.IsTranslationComplete`, `.part` / `.done` izolasyonu, Studio'dan tek tık kısmi sıfırlama ve `DirectTranslatorFallback` / `MicroRecovery`.
- [x] **Gözlemlenebilirlik, Canlı Kur & SSE Token Doğruluğu:** OpenTelemetry + `.NET Aspire Dashboard`, `ExchangeRateService` (Frankfurter ECB + ExchangeRate-API) ile canlı USD/TRY maliyet tahmini, `ResilienceChatClient` kümülatif SSE `UsageContent` + gizli reasoning token muhasebesi + `Retry-After` / timeout dayanıklılığı.
- [x] **Güvenli Kapanış, Sıfır-Yapılandırma & Yayın Hijyeni:** Checkpoint/OTLP safe flush shutdown, kök `MIT` [LICENSE](LICENSE), `appsettings.example.json` şablon katmanı (`Evren` + `Gemini`/`OpenAI`/`Ollama`/`Custom`), `PathSanitizer` / `SafeXmlLoader` sertleştirmesi, `.NET 10 SIGTERM` (`PosixSignalRegistration`) ve `IHttpClientFactory` / `IOptionsMonitor` / CPM (`Directory.Packages.props`).
- [x] **Çapraz Platform Test & Tek-Dosya Release Otomasyonu:** Test paketi `Glossator.Tests` (`net10.0`, `472/472`) ve `Glossator.App.Tests` (`net10.0-windows`, `12/12`) olarak toplam **`484 / 484`** testle doğrulandı; `.github/workflows/ci.yml` içinde `windows-latest` + `ubuntu-latest` (`472` test) ve `.github/workflows/release.yml` içinde sıkıştırılmış tek-dosya self-contained Win Studio (`GLOSSATOR_SMOKE_TEST=1`) + Win/Linux CLI + `SHA256SUMS.txt` eklendi.

---

## Sıradaki Kilometre Taşları

### v1.3.0 — Gelişmiş Sözlük Çıkarımı & Kararlı Sürüm
- [ ] **Akıllı Sözlük Çıkarım Motoru (`Glossary Extraction`):** Kitap metninden otomatik aday terim çıkarım mantığının geliştirilmesi, bağlam/frekans puanlama, gürültü eleme ve mevcut onaylı sözlükle akıllı tekilleştirme.
- [ ] **Beta Geri Bildirim & Cilalama:** Açık beta kullanımından gelen kenar durum hata düzeltmeleri, UI ergonomi iyileştirmeleri ve kararlı `v1.3.0` etiketiyle yayın.

### v1.4 — EPUB Yapısal Derinlik & Alan Profilleri
- [ ] **`nav.xhtml` & `toc.ncx` Başlık Yerelleştirmesi:** Yalnızca dosya referanslarını değil içindekiler başlık metinlerini de bölüm çevirisiyle otomatik senkronize etme.
- [ ] **Kenar Durum Tipografisi:** Görsel `alt` metinlerinin, dipnot (`footnote` / `aside epub:type="footnote"`) bloklarının ve yumuşak tireleme (soft hyphen) kırılmalarının özel işlenmesi.
- [ ] **Hazır Alan Profilleri:** Teknik yazılım yanında **Tıp**, **Hukuk** ve **Akademik Sosyal Bilimler** için hazır prompt ve sözlük başlangıç paketleri.

### v2.0 — Profesyonel CAT Yetenekleri & Sağlayıcı Yedekliliği
- [ ] **Bilingual (İki Dilli) Okuma Çıktısı:** Öğrenme amaçlı yan yana / paragraf altı İngilizce-Türkçe paralel EPUB çıktı modu.
- [ ] **TMX & Fuzzy Translation Memory:** Önceki kitaplardaki çeviri birimlerini (`.tmx`) dışa/içe aktarma ve yüksek benzerlikte cümle düzeyinde önbellek eşleştirme.
- [ ] **Otomatik Sağlayıcı Failover:** Birincil sağlayıcıda kalıcı `429` veya servis kesintisi yaşandığında kullanıcının tanımladığı yedek (secondary) sağlayıcıya otomatik devretme (circuit-breaker fallback).
