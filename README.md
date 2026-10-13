# VoiceType

مساعد إملاء صوتي لـ Windows: تنقر على زر الميكروفون، وتتكلم بالعربي (المصري خصوصًا) أو بالإنجليزي، فيُكتب النص في أي برنامج.

الخطة الكاملة في `docs/VoiceType_Project_Spec.md`.

## البرنامج الجاهز

البرنامج في مجلد **`..\VoiceType (البرنامج)\`** بجوار هذا المجلد. افتح `VoiceType.exe` فقط، والتعليمات في `اقرأني.txt` هناك. المجلد محمول: انسخه كما هو على فلاشة.

أدوات المطور (`VoiceTypeLab.exe` للمقارنة، و`voicetype-cli.exe` للوكيل البرمجي) ليست في مجلد البرنامج. تُبنى بـ `scripts\build.ps1` إلى `release\dev-tools`، وتعليمات الوكيل في `docs/AGENT_PROMPT.md`.

## الحالة الحالية

| الجزء | الحالة |
|---|---|
| نظام المزودات (Custom Provider + 8 مزودات مدمجة) | ✅ جاهز ومختبر |
| إعادة المحاولة، Circuit breaker، سلسلة البدائل | ✅ جاهز ومختبر |
| معالجة النص (الترقيم العربي، قاموس المصطلحات، الأوضاع) | ✅ المستوى الأول |
| تقييم الجودة (WER / CER / المصطلحات الإنجليزية) | ✅ جاهز |
| تخزين المفاتيح المشفر (DPAPI) | ✅ جاهز |
| النماذج على الجهاز (Whisper، مجاني وبدون إنترنت) | ✅ جاهز ومختبر |
| VoiceType Lab (واجهة المقارنة) + أداة voicetype-cli للوكيل | ✅ المرحلة 0 |
| تطبيق Windows (زر الميكروفون العائم، الإدخال في البرامج) | ⏳ المرحلة 1 — التالي |

## للمطور

المتطلبات: Windows 10/11 و [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
.\scripts\build.ps1     # بناء + اختبارات + إنتاج release\VoiceType و release\dev-tools
.\scripts\compare.ps1 help
```

## إضافة أي مزود

كل المزودات تُعرَّف بملف JSON، بدون كود. الشرح والأمثلة في [`docs/custom-provider.md`](docs/custom-provider.md).

## بنية المشروع

```
src/
  VoiceType.Core/         الواجهات المشتركة: ISpeechEngine، الأخطاء، الصوت
  VoiceType.Providers/    محرك المزود المخصص، الكتالوج المدمج، إعادة المحاولة، البدائل، اختبار المفاتيح
  VoiceType.Text/         الترقيم العربي، قواميس المصطلحات، أوضاع المعالجة
  VoiceType.Settings/     مخزن المفاتيح المشفر (DPAPI)، إعدادات المختبر، أماكن الملفات
  VoiceType.Speech/       التفريغ المحلي (Whisper.net)، تنزيل النماذج، تسريع كرت الشاشة
  VoiceType.Evaluation/   WER / CER، تشغيل المقارنة، التقارير (HTML / Markdown / CSV)
tools/
  VoiceType.Lab/          برنامج VoiceType Lab (WPF، واجهة عربية)
  VoiceType.Cli/          أداة voicetype-cli لسطر الأوامر (للوكيل البرمجي والمطور)
tests/
  VoiceType.Tests/        اختبارات بدون إنترنت (ردود مزودات وهمية)
docs/                     التوثيق
scripts/                  build.ps1 و compare.ps1
```
