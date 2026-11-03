# أمر تحديث صدى على أي جهاز من أجهزة محمد

انسخ النص اللي تحت الخط، وابعته لـ Claude على الجهاز اللي محتاج تحديث.

---

حدّث برنامج «صدى» (VoiceType) على الجهاز ده لآخر نسخة من GitHub (v2.17 أو أحدث). اشتغل بالترتيب، ولو أي خطوة فشلت وقّف وقولّي السبب قبل ما تكمل.

**قواعد لازم تلتزم بيها:**
- ممنوع تطبع أو تسجل أو تكتب أي مفتاح API في أي ملف. ممنوع تفتح أو تقرأ `keys.dat`. الطريقة الوحيدة لحفظ مفتاح هي `voicetype-cli.exe keys set <provider> <key>`.
- ممنوع تفعّل أي خدمة مدفوعة (`paid`)، وممنوع تنزّل أي حاجة بفلوس.
- ممنوع تمسح أي حاجة في مجلد النماذج المشترك مع Vibe.
- اعمل نسخة احتياطية من أي ملف إعدادات قبل ما تعدّله.

**0) اتأكد من الجهاز:** اكتب اسم الجهاز والمعالج وكرت الشاشة والرامات، وقارنهم بالجدول ده:

| | المكتبي | اللاب |
|---|---|---|
| الاسم | DESKTOP-1UECKES | DESKTOP-L56NQLV |
| العتاد | Ryzen AI 9 HX 370، Radeon 890M (AMD، Vulkan بس) | Core Ultra 9 275HX، RTX 5070 Ti 12GB |
| الكود | `D:\my pro\مساعد الإملاء\VoiceType (الكود المصدري)` | `E:\mine\sada\VoiceType (الكود المصدري)` |
| البرنامج | `D:\my pro\مساعد الإملاء\VoiceType (البرنامج)` | `E:\mine\sada\VoiceType (البرنامج)` |
| النماذج المشتركة مع Vibe | `D:\VibeModels` | `E:\VibeModels` |

لو الجهاز مش واحد من دول، وقّف وقولّي.

**1) هات التحديث:** في مجلد الكود شغّل `git status`. لو فيه تعديلات محلية، متمسحهاش، وقّف وقولّي. لو نظيف، شغّل `git pull --ff-only`، وبعدين اكتب آخر 4 سطور من `git log --oneline`.

**2) أداة البناء:** `dotnet --list-sdks` لازم يطلع فيه 10.x. لو مش موجود، اسألني الأول قبل ما تسطّب `winget install Microsoft.DotNet.SDK.10`. الأداة دي ببلاش.

**3) الاختبارات:** `dotnet test tests/VoiceType.Tests -c Release`. لازم كل الاختبارات تنجح (211 أو أكتر).

**4) البناء:** اقفل صدى (`Get-Process VoiceType -ErrorAction SilentlyContinue | Stop-Process`)، وبعدين شغّل:
```
dotnet publish src/VoiceType.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "<مجلد البرنامج>"
```
اتأكد إن `<مجلد البرنامج>\crispasr\VoiceType.CrispHost.exe` اتعمل. ده البرنامج المساعد لنموذج Cohere، وبيتبني مع صدى.

**5) الإعدادات:** اقرأ `data\settings.json`، وده مفيهوش مفاتيح. اتأكد من القيم دي:
- `primary` = `gemini|gemini-3.5-transcribe`
- `fallback` = `local|cohere-transcribe-arabic-q4_k-imatrix.gguf` (محمد جربه واختاره احتياطي بعد نماذج الإنترنت، 4 نوفمبر 2026). لو Cohere لسه مش جاهز على الجهاز (الخطوة 7)، سيب `local|ggml-large-v3-q5_0.bin` مؤقتًا لحد ما يبقى جاهز، وبعدين غيّره.
- `useFallback` = true
- `liveTypingBeta` = false

لو أي قيمة مختلفة، اعمل نسخة احتياطية الأول، وبعدين عدّل الحقول دي بس.

**6) تسريع كرت الشاشة:** اتأكد إن `C:\Windows\System32\vulkan-1.dll` و `<البرنامج>\runtimes\vulkan\win-x64\ggml-vulkan-whisper.dll` موجودين. لو التاني مش موجود، قولّي أسطّبه من جوه البرنامج: الإعدادات ← النماذج المحلية ← تسريع كرت الشاشة.

**7) نموذج Cohere Arabic Q4:**
- لو `cohere-transcribe-arabic-q4_k-imatrix.gguf` موجود في مجلد النماذج المشترك وحجمه 1510365440 بايت، ومحرك CrispASR مش موجود (`<البرنامج>\crispasr\crispasr.dll`)، قولّي أدوس «تنزيل» على Cohere من الإعدادات ← النماذج المحلية. صدى هينزّل المحرك بس (29 ميجا) ويتأكد من بصمته.
- لو النموذج مش موجود، قولّي إنه متاح، وإن تنزيله من نفس المكان (1.5 جيجا، ببلاش). متنزلوش من غير ما أوافق.
- أول ما النموذج والمحرك يبقوا جاهزين، اقفل صدى، وخلّي `fallback` في `data\settings.json` = `local|cohere-transcribe-arabic-q4_k-imatrix.gguf`، وبعدين افتحه تاني.

**8) شغّل صدى:** `Start-Process` على `VoiceType.exe`، واتأكد إنه شغال (`Responding = True`).

**9) التقرير:** اكتبلي:
- رقم آخر commit
- عدد الاختبارات اللي نجحت
- حجم `VoiceType.exe`
- قيم الإعدادات في الخطوة 5
- حالة تسريع كرت الشاشة
- حالة Cohere (النموذج والمحرك)

**ملاحظات (متعدلش أي كود):**
- لو الكتابة في أي برنامج فشلت، هتلاقي في `data\logs\dictation.log` سطر `insert failed: <السبب> app=<البرنامج>`. ابعتهولي.
- سطر `local:` بيقول أنهي نموذج محلي كتب، وأخد قد إيه.
