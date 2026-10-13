# إضافة أي مزود تفريغ (Custom Provider)

كل المزودات في VoiceType، المدمجة والمخصصة، مبنية بنفس الطريقة: **ملف تعريف JSON** يصف كيف يُرسل الصوت وكيف يُقرأ النص. المزودات المدمجة موجودة في `src/VoiceType.Providers/Catalog/` ويمكن استخدامها كأمثلة.

لإضافة مزود جديد لا تحتاج كتابة كود. أنشئ ملف `.json` ثم أضفه بإحدى الطريقتين:

- من برنامج **VoiceType Lab**: تبويب "المفاتيح" ← "إضافة خدمة مخصصة من ملف…".
- من سطر الأوامر: `voicetype-cli providers add my-provider.json`

يُحفظ الملف في `%APPDATA%\VoiceType\providers\`.

## الحقول

| الحقل | إجباري | الوصف |
|---|---|---|
| `id` | نعم | معرّف بالإنجليزية بدون مسافات. إذا طابق معرّف مزود مدمج يحل محله |
| `name` | نعم | الاسم الظاهر |
| `description`, `website` | لا | وصف ورابط |
| `variables` | لا | متغيرات بقيم افتراضية، مثل `base_url`. المستخدم يستطيع تغييرها لكل نسخة |
| `models` | لا | قائمة `{ "id": "...", "notes": "..." }` |
| `defaultModel` | لا | النموذج الافتراضي |
| `timeoutSeconds` | لا | المهلة (60 افتراضيًا) |
| `request.url` | نعم | العنوان، ويقبل المتغيرات |
| `request.method` | لا | `POST` (افتراضي) أو `PUT` |
| `request.auth.type` | لا | `bearer` (افتراضي) / `header` / `query` / `none` |
| `request.auth.name` | مع header/query | اسم الـHeader أو معامل الـQuery، مثل `xi-api-key` |
| `request.auth.prefix` | لا | بادئة قبل المفتاح، مثل `Token` |
| `request.auth.optional` | لا | `true` = أرسل بدون مصادقة إذا لم يوجد مفتاح (للخوادم المحلية) |
| `request.headers` | لا | Headers إضافية |
| `request.query` | لا | معاملات Query |
| `request.body.type` | لا | `multipart` (افتراضي) / `json` / `raw` |
| `request.body.fileField` | multipart | اسم حقل الملف (`file` افتراضيًا) |
| `request.body.fields` | multipart | الحقول النصية |
| `request.body.template` | json | قالب JSON كامل، ويجب أن يحتوي `{audio_base64}` |
| `request.body.contentType` | raw | نوع المحتوى (`{audio_mime}` افتراضيًا) |
| `response.textPath` | نعم | مسار النص في الرد. `$` تعني أن الرد كله نص |
| `response.errorPath` | لا | مسار رسالة الخطأ |
| `response.costPath` | لا | مسار التكلفة إن أعادها المزود |
| `capabilities.formats` | لا | الصيغ المقبولة. إذا تُرك فارغًا تُقبل كل الصيغ |
| `capabilities.maxFileMb` | لا | أقصى حجم |
| `capabilities.languageRequired` | لا | هل اللغة إجبارية |
| `capabilities.supportsPrompt` | لا | هل يقبل نصًا توجيهيًا |

## المتغيرات

| المتغير | القيمة |
|---|---|
| `{model}` | النموذج المختار |
| `{language}` | رمز اللغة مثل `ar`، أو فارغ عند التعرف التلقائي |
| `{prompt}` | النص التوجيهي (فقط إذا كان `supportsPrompt: true`) |
| `{temperature}` | درجة الحرارة إن حُددت |
| `{audio_base64}` | الصوت بترميز base64 |
| `{audio_format}` | `wav` / `mp3` / `flac` ... |
| `{audio_mime}` | `audio/wav` / `audio/mpeg` ... |
| `{file_name}` | اسم الملف |
| `{api_key}` | المفتاح، لحالات نادرة يوضع فيها داخل الـbody |
| أي اسم في `variables` | قيمته، مثل `{base_url}` |

### قواعد مهمة

- **الحقل الذي يصبح فارغًا يُحذف تلقائيًا** من الطلب، مثل `language` عند اختيار "تلقائي".
- داخل قالب JSON: `"{temperature:number}"` يصبح رقمًا، و`"{flag:bool}"` يصبح true/false.
- المسارات: `text`، `error.message`، `results.channels[0].alternatives[0].transcript`، `choices[0].message.content`.
- إذا كان المسار يشير إلى مصفوفة مقاطع فيها `text`، تُدمج كلها بمسافة.

## أمثلة

### 1. خدمة متوافقة مع OpenAI، أو خادم محلي

لا تحتاج ملفًا: استخدم المزود المدمج `openai-compatible` مع عنوان الخادم.

```
voicetype-cli models add openai-compatible whisper-large-v3 --var base_url=http://localhost:8000/v1
```

### 2. خدمة تستقبل JSON مع base64

```json
{
  "id": "acme",
  "name": "ACME Speech",
  "variables": { "base_url": "https://api.acme.com" },
  "request": {
    "url": "{base_url}/v2/recognize",
    "auth": { "type": "header", "name": "X-Api-Key" },
    "body": {
      "type": "json",
      "template": {
        "config": { "model": "{model}", "languageCode": "{language}" },
        "audio": { "content": "{audio_base64}" }
      }
    }
  },
  "response": { "textPath": "results[0].alternatives[0].transcript", "errorPath": "error.message" },
  "capabilities": { "formats": [ "wav", "flac" ] }
}
```

### 3. خدمة تستقبل الصوت خامًا، والمفتاح في الرابط

```json
{
  "id": "raw-demo",
  "name": "Raw Demo",
  "request": {
    "url": "https://stt.example.com/transcribe",
    "auth": { "type": "query", "name": "api_key" },
    "query": { "lang": "{language}" },
    "body": { "type": "raw" }
  },
  "response": { "textPath": "transcript" }
}
```

### 4. Hugging Face Inference Endpoint خاص

لتشغيل نموذج مصري متخصص على خادمك الخاص في Hugging Face، أنشئ ملف `hf-endpoint.json`:

```json
{
  "id": "hf-endpoint",
  "name": "My HF Endpoint",
  "variables": { "base_url": "https://xxxx.endpoints.huggingface.cloud" },
  "request": {
    "url": "{base_url}",
    "auth": { "type": "bearer" },
    "body": { "type": "raw" }
  },
  "response": { "textPath": "text", "errorPath": "error" }
}
```

ثم:

```
voicetype-cli providers add hf-endpoint.json
voicetype-cli keys set hf-endpoint hf_xxxxxxxx
```

## التحقق قبل الإرسال

```
voicetype-cli providers list    ← يعرض المزودات ويكشف أخطاء ملفات التعريف
voicetype-cli providers show <id>
voicetype-cli compare preview   ← يعرض الطلب الذي سيُرسل (المفاتيح مخفية) بدون إرساله
```
