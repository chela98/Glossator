# Yapılandırma, AI Sağlayıcılar, Reasoning ve Prompt Kılavuzu

Bu belge; Glossator Studio ve CLI için ayarların nasıl yüklendiğini, hangi OpenAI uyumlu sağlayıcıların nasıl bağlandığını, **Ping / Otomatik Model Keşfi** özelliklerini, **Akıl Yürütme (Thinking / Reasoning Effort)** kademelerini ve kod derlemeden **Prompt** özelleştirmeyi açıklar.

---

## 1. Yapılandırma Öncelik Zinciri

Yapılandırma dosyaları aşağıdan yukarıya birbirini ezer (en alttaki kazanır):

| Öncelik | Kaynak | Açıklama |
| :---: | :--- | :--- |
| 1 | `appsettings.example.json` (`AppContext.BaseDirectory`) | Sıfır-yapılandırma temel şablon (`Evren` varsayılan + `Gemini`, `OpenAI`, `Ollama`, `Custom`) |
| 2 | `appsettings.json` (`AppContext.BaseDirectory`) | Uygulama yanındaki yerel ayarlar (`.gitignore` kapsamındadır) |
| 3 | `appsettings.Development.json` | Geliştirme ortamı ek ayarları |
| 4 | *User Secrets* (yalnızca `Glossator.Cli` Dev) | `glossator-cli-secrets-9a8b7c6d` |
| 5 | **`~/.glossator/user_settings.json`** | Studio'dan kaydedilen aktif ayarlar (`GLOSSATOR_HOME` ile değişir) |
| 6 | **Ortam Değişkenleri** | Örn. `GLOSSATOR_HOME`, `GLOSSATOR_DB`, `GlossatorAi__ActiveProvider` |

> [!TIP]
> Hem **Glossator Studio** hem **Glossator CLI** ilk açılışta doğrudan [appsettings.example.json](../Glossator.Cli/appsettings.example.json) temel şablonunu yedek olarak yükler (`Evren` varsayılan aktif profil, yanında `Gemini`, `OpenAI`, `Ollama`, `Custom` hazır). EVREN veya başka bir sağlayıcı için **Ayarlar** sekmesinden API anahtarınızı girip **Ayarları Kaydet** diyerek `~/.glossator/user_settings.json` dosyasına kaydedebilirsiniz; ayrıca `SettingsViewModel` yapılandırma hiç bulunmasa bile `Evren`, `Gemini`, `OpenAI` ve `Ollama` ön ayarlarını otomatik sunar.

### Taşınabilir Ortam Değişkenleri (`GlossatorPaths`)
* `GLOSSATOR_HOME`: Varsayılan `~/.glossator` çalışma dizinini başka bir klasöre taşır (`user_settings.json` ve varsayılan `glossator.db` buradadır).
* `GLOSSATOR_DB`: Merkezi SQLite veritabanı (`glossator.db`) dosya yolunu doğrudan ezer.

---

## 2. Sağlayıcı (Provider) Profilleri

`AiServiceExtensions` (.NET 10 `Microsoft.Extensions.AI.OpenAI`) tüm **OpenAI-compatible** (`/v1`) uç noktaları destekler.

### Hazır Sağlayıcı Örnekleri

| Sağlayıcı | Endpoint | Örnek Varsayılan Model | Notlar |
| :--- | :--- | :--- | :--- |
| **OpenAI** | `https://api.openai.com/v1` | `gpt-4o`, `gpt-5-mini` | Resmi API anahtarı (`sk-...`) |
| **Google Gemini** | `https://generativelanguage.googleapis.com/v1beta/openai/` | `gemini-2.5-flash` | Google AI Studio `AIza...` anahtarı |
| **Ollama (Yerel)** | `http://localhost:11434/v1` | `qwen2.5:14b` | Çevrimdışı; anahtar boşsa `dummy-local-key` kullanılır |
| **LM Studio / vLLM** | `http://localhost:1234/v1` / `http://localhost:8000/v1` | Yüklenen model ID | `GlossatorAiOptions.IsLocalEndpoint` yerel IP/localhost'ta anahtar zorunluluğunu atlar |
| **OpenRouter / Groq** | `https://openrouter.ai/api/v1` / `https://api.groq.com/openai/v1` | Model kimliği | Tam OpenAI Chat Completions uyumlu |
| **EVREN** | `https://evren-llmapi.ssyz.org.tr/v1` | `deepseek-v4.1-flash` | Varsayılan aktif profil; EVREN API anahtarı gerektirir (yoksa Ayarlar'dan tek tıkla değiştirilebilir) |

### Örnek `GlossatorAi` Yapılandırması (`user_settings.json`)

```json
{
  "GlossatorAi": {
    "ActiveProvider": "Gemini",
    "Providers": {
      "Gemini": {
        "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/",
        "ApiKey": "AIzaSy...",
        "DefaultModel": "gemini-2.5-flash"
      },
      "Ollama": {
        "Endpoint": "http://localhost:11434/v1",
        "ApiKey": "ollama",
        "DefaultModel": "qwen2.5:14b"
      }
    },
    "Agents": {
      "StyleAnalyst": {
        "Provider": "",
        "ModelId": "",
        "ReasoningEffort": "low"
      },
      "Translator": {
        "Provider": "Gemini",
        "ModelId": "gemini-2.5-flash",
        "ReasoningEffort": "none"
      },
      "Reviewer": {
        "Provider": "Ollama",
        "ModelId": "qwen2.5:14b",
        "ReasoningEffort": ""
      }
    }
  }
}
```

### Sağlayıcı Çözümleme Mantığı (`GlossatorAiOptions.ResolveAgent`)
1. Ajanın kendi `Provider` alanı doluysa ve o sağlayıcının geçerli bir gerçek `ApiKey` değeri (`YOUR_...` yer tutucusu değilse veya yerel endpoint ise) varsa o sağlayıcı seçilir.
2. Aksi durumda sistem güvenli şekilde `ActiveProvider` profiline geri düşer (ör. `SettingsViewModel` içinde `(Aktif Sağlayıcıyı İzle)` seçildiğinde `Provider` boş kaydedilir).
3. Ajanın `ModelId` alanı boş bırakılırsa hedef sağlayıcının `DefaultModel` değeri kullanılır.

---

## 3. Sağlayıcı Ping & Otomatik Model Keşfi (`ProviderDiscoveryService`)

Glossator Studio **Ayarlar → Sağlayıcı Bağlantı Ayarları** paneli üzerinden doğrudan bağlantı testi ve model taraması yapılabilir:

1. **Ping (Bağlantı Testi):**
   * Önce `GET {endpoint}/models` çağrısı atarak gecikme süresini (`ms`) ve API anahtarını doğrular; yanıt `200 OK` dönerse bulunan modelleri de önbelleğe alarak ajan dropdown'larını otomatik doldurur.
   * Özel proxy veya gateway sunucusu `/models` için `404`, `405` veya `501` döndürürse ve **Varsayılan Model** alanı doluysa hafif `POST {endpoint}/chat/completions` (`max_tokens: 1`; model gerektiriyorsa otomatik `max_completion_tokens: 1` yedek denemesi) ile gerçek bağlantı testi yapar.
2. **Modelleri Çek (`/models`):**
   * OpenAI (`data[].id`) ve Ollama (`models[].name`) listelerini ayrıştırır, Gemini uyumlu yanıtlardaki `models/` önekini temizler, yinelenenleri ayıklayıp alfabetik sıralar.
   * Çekilen modeller hem **Varsayılan Model** hem de ilgili sağlayıcıyı kullanan ajanların **Özel Model ID** açılır kutularına (`ComboBox IsEditable="True"`) dolar.
   * Sağlayıcı `/models` uç noktasını kapatmışsa kutuya elle istediğiniz model adını yazmaya devam edebilirsiniz; mevcut model girişleriniz liste güncellendiğinde silinmez.

---

## 4. Akıl Yürütme Yoğunluğu (`ReasoningEffort`)

[AiServiceExtensions.cs](../Glossator.AI/Extensions/AiServiceExtensions.cs) içindeki `MapReasoningEffortLevel`, ajana özel seçilen değeri hem `ChatOptions.AdditionalProperties["reasoning_effort"]` hem de OpenAI SDK `ChatCompletionOptions.ReasoningEffortLevel` düzeyinde doğrudan API isteğine işler.

Studio arayüzünde sunulan seçenekler:

| Seçenek | Etkisi | Uyumluluk Notu |
| :--- | :--- | :--- |
| **`(Varsayılan)`** | `reasoning_effort` parametresi göndermez | Sağlayıcı/model varsayılanı neyse onu kullanır (en güvenli seçenek) |
| **`none`** | Düşünme/akıl yürütmeyi tamamen kapatır | Ör. **Gemini 2.5 Flash / Flash-Lite** ve destekleyen OpenAI/proxy modellerinde **TTFT süresini ve thinking token maliyetini düşürür**. Gemini 2.5 Pro veya Gemini 3.x serisinde API kapatmaya izin vermeyebilir |
| **`minimal`** | Çok hafif düşünme bütçesi | Bazı Gemini 3 / yeni nesil modeller `minimal` yerine minimum `low` ister |
| **`low`** | Düşük akıl yürütme | Hızlı çeviri (`Translator`) ve küçük kesitlerde iyi hız/kalite dengesi |
| **`medium`** | Orta akıl yürütme | Dengeli redaksiyon (`Reviewer`) ve teknik karmaşıklık |
| **`high`** | Yüksek akıl yürütme | Zor üslup analizi (`StyleAnalyst`) veya yoğun metinler |
| **`xhigh`** | En yüksek akıl yürütme | Yalnızca destekleyen reasoning modellerinde (desteklemeyen uç noktalarda HTTP 400 dönebilir) |

> [!NOTE]
> `user_settings.json` içinde özel başka bir değer tanımlıysa (`SettingsViewModel`) bunu kaybetmez ve listede korur.

---

## 5. Prompt Mimarisi ve Disk-First Yükleyici (`PromptLoader`)

Yapay zekâ yönlendirmeleri C# koduna gömülü değildir. Üç ajan için üç ayrı Markdown istemi vardır:
* `StyleAnalystPrompt.md`: Bölüm girişinden yazar tonu, hitap biçimi (sen/siz, aktif mühendis sesi) ve üslup parmak izi çıkarır.
* `TranslatorPrompt.md`: Blok sırası (`[BLOK_xxx]`), `İngilizce (Türkçe)` başlık kuralı, sahte dostlar (false friends) ve `⟦PH_xxxxxx⟧` koruma kurallarını belirler.
* `ReviewerPrompt.md`: Keyfi müdahaleyi yasaklar; C# tarafından yakalanamayan anlam/ton sapmalarını RFC 6902 benzeri JSON Patch (`{"isApproved": true, "patches": [...]}`) veya reddetme geri bildirimiyle düzeltir.

### Yükleme Hiyerarşisi ([PromptLoader.cs](../Glossator.AI/Prompts/PromptLoader.cs))
1. `{AppContext.BaseDirectory}/Prompts/{dosyaAdı}` (Kullanıcı özelleştirmesi / Studio **Prompt Editörü** kayıt yeri)
2. `{AppContext.BaseDirectory}/{dosyaAdı}`
3. **Embedded Resource Fallback:** `Glossator.AI.dll` içine derlenmiş fabrika istemi.

*(Not: Rastgele çalışma dizinlerinden eski istem gölgelemesi yaşanmaması için `CurrentDirectory` taranmaz.)*

### Farklı Disiplinlere Uyarlama (Tıp, Biyoloji, Hukuk, Mühendislik)
Kod derlemeden doğrudan **Ayarlar → Prompt Editörü** üzerinden veya `Prompts/` klasöründeki dosyaları düzenleyerek alanı değiştirebilirsiniz:
* **Tıp / Klinik:** `TranslatorPrompt.md` içindeki başlık örneklerini `Myocardial Infarction (Miyokard Enfarktüsü)` gibi güncelleyin; *discharge* → *taburcu / akıntı* gibi bağlam kuralları ekleyin.
* **Hukuk:** `Due Process (Adil Yargılanma Hakkı)`, `Force Majeure (Mücbir Sebep)` ve Latince deyimleri (`de facto`, `pacta sunt servanda`) koruyun.
* **Mühendislik / Yaşam Bilimleri:** `<pre>`, `<code>`, `<math>` ve `<svg>` içindeki formül/sekanslar zaten AST maskelemesi ile kilitlenir; `Terim Sözlüğü` sayfasından alan terminolojisini `AlwaysEnglish` / `ProgressiveGloss` / `AlwaysTurkish` kurallarıyla yönetmeniz yeterlidir.
