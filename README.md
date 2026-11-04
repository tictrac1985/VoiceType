# صدى (VoiceType)

مساعد إملاء وتفريغ صوتي عربي لـ Windows، ومعمول للعامية المصرية بالذات: تدوس على الميكروفون العائم وتتكلم، والكلام يتكتب في أي برنامج. وكمان بيفرّغ الملفات والاجتماعات واللينكات.

الخطة الكاملة وسجل التغييرات في [`docs/VoiceType_Project_Spec.md`](docs/VoiceType_Project_Spec.md).

## المميزات

- **إملاء في أي برنامج:** أوامر صوتية («امسح آخر جملة»)، و«صيغها لي» (فصحى، أو إيميل، أو ترجمة للإنجليزي)، وإعداد خاص لكل برنامج.
- **نماذج على الإنترنت:** Gemini ببلاش بمفاتيح مجانية، وتتبدّل تلقائي بين أكتر من مفتاح.
- **نماذج على جهازك من غير نت:**
  - Cohere Transcribe Arabic، وده الأدق والأسرع للعربي، وبيشتغل بمحرك CrispASR.
  - Whisper Large-v3 و Turbo.
  - بتسريع كرت الشاشة (Vulkan) على AMD و NVIDIA و Intel.
- **تفريغ الملفات:** ملف واحد أو ملفات كتير مرة واحدة، صوت أو فيديو، أو لينك (يوتيوب، فيسبوك، X…) بأداة yt-dlp.
- **مين اللي بيتكلم:** «المتكلم 1، المتكلم 2…» في الملفات والاجتماعات.
- **تسجيل الاجتماعات:** صوت الجهاز وصوتك مع بعض، والكلام بيظهر وانت بتسجّل، وبيقترح التسجيل لما Zoom أو Teams أو Meet يشتغلوا. وفي الآخر ملخص وقرارات ومهام.
- **الذكاء الاصطناعي للملخص والصياغة والترجمة:**
  - Gemini.
  - Ollama، على الجهاز أو موديلاته السحابية.
  - LM Studio، على الجهاز أو بـ LM Link.
- **التصدير:** TXT و SRT و VTT (بطول سطر مناسب للريلز) و JSON و HTML و PDF و Word، وكمان طباعة.
- **تحديث من جوه البرنامج:** الإعدادات ← التحديثات. بيتأكد من بصمة الملف، ومبيلمسش بياناتك.

## التسطيب

1. نزّل `Sada-win-x64.zip` من [آخر إصدار](../../releases/latest)، وفكّه في أي فولدر.
2. لازم يكون عندك [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0).
3. شغّل `VoiceType.exe`، وضيف مفتاح Gemini مجاني أو نزّل نموذج محلي من الإعدادات.

المفاتيح بتتحفظ مشفّرة على جهازك بس (Windows DPAPI)، في `data\` جنب البرنامج. الفولدر ده عمره ما بيترفع ولا بيتلمس في التحديث.

## للمطور

المتطلبات: Windows 10/11 و [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet test tests\VoiceType.Tests -c Release
dotnet publish src\VoiceType.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o <فولدر البرنامج>
.\scripts\release.ps1   # نشر نسخة جديدة على GitHub Releases (بعد ما تغيّر Version في Directory.Build.props)
```

إضافة أي مزود تفريغ بملف JSON من غير كود: [`docs/custom-provider.md`](docs/custom-provider.md).

## بنية المشروع

```
src/
  VoiceType.App/          البرنامج (WPF + WebView2): الميكروفون العائم، الواجهة، الإدخال في البرامج، التحديث
  VoiceType.Core/         الواجهات المشتركة: ISpeechEngine، الأخطاء، الصوت
  VoiceType.Providers/    المزودات، البدائل، إعادة المحاولة، الذكاء الاصطناعي للنص (Gemini / Ollama / LM Studio)
  VoiceType.Text/         الترقيم العربي، المصطلحات، الأوامر الصوتية، تنسيق النص
  VoiceType.Settings/     الإعدادات، والمفاتيح المشفرة (DPAPI)، وأماكن الملفات
  VoiceType.Speech/       Whisper.net، و Cohere (CrispASR)، والمتكلمين، والملفات، والاجتماعات، واللينكات، والتصدير
  VoiceType.CrispHost/    برنامج مساعد منفصل لمكتبة CrispASR (بيشتغل مخفي)
  VoiceType.Evaluation/   قياس الدقة (WER / CER) والمقارنة
tools/                    VoiceType Lab و voicetype-cli
tests/                    الاختبارات
docs/                     التوثيق
scripts/                  build.ps1 و compare.ps1 و release.ps1
```
