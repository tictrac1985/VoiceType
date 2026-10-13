# Prompt for my coding agent — VoiceType

You are helping me operate **VoiceType**, a Windows voice-dictation program for Arabic (mainly Egyptian dialect mixed with English technical words). I click a floating mic button, speak, click again, and the text is typed into whatever app has the cursor. It also transcribes audio files.

## Where things are
- Program folder (portable, can be copied to a USB stick): `D:\my pro\مساعد الإملاء\VoiceType (البرنامج)\`
  - `VoiceType.exe` — **the only program I use** (floating mic + file transcription + models page).
  - `data\` — ALL data: settings, encrypted keys, local models (`data\models`), transcripts, logs (`data\logs\errors.log`).
  - `runtimes\` and `WebView2Loader.dll` — must stay next to `VoiceType.exe`.
- Source code: `D:\my pro\مساعد الإملاء\VoiceType (الكود المصدري)\` (plan: `docs\VoiceType_Project_Spec.md`, custom provider guide: `docs\custom-provider.md`).
- Developer tools are NOT in the program folder. Build them when needed: run `scripts\build.ps1` in the source folder → `release\dev-tools\voicetype-cli.exe` and `VoiceTypeLab.exe`.
  To make the CLI use the program's data, set `VOICETYPE_HOME` to the program's `data` folder first, e.g. in PowerShell:
  `$env:VOICETYPE_HOME = "D:\my pro\مساعد الإملاء\VoiceType (البرنامج)\data"`

## First-time check
1. `dotnet --list-runtimes` must show `Microsoft.WindowsDesktop.App 10.x`. If not, install it:
   `winget install Microsoft.DotNet.DesktopRuntime.10`
2. Run `voicetype-cli.exe help` and read the command list.
3. WebView2 runtime is needed for the main window (already part of Windows 11). If the window says it is missing, install it: `winget install Microsoft.EdgeWebView2Runtime`

## Rules
- **Free first. I do not want to pay anything for now.** Prefer models on this computer (`voicetype-cli.exe local ...`) and free / free-tier providers. Never enable a provider marked `paid` in `providers list`, and never add anything that requires payment, unless I explicitly approve it.
- **Never print, log, echo, or write my API keys into any file.** The only allowed way to store a key is `voicetype-cli.exe keys set <provider> <key>` (it encrypts with Windows DPAPI).
- If I give you a key, save it with `keys set`, report only the masked value and the test result.
- Do not delete anything inside `data\` (recordings, transcripts, results, models) unless I ask.
- Before `compare run`, count requests = recordings × ACTIVE models (`samples list`, `models list`). **If more than 100, ask me first** (it costs money).
- Exit code `2` from `keys test` means the key is invalid.
- Custom providers must follow `docs\custom-provider.md`; always add them with `providers add` (it validates the file). Never edit files inside `data\` by hand.
- After any change, show me the result of `voicetype-cli.exe app show`.
- `app primary|fallback|files` refuse to run while VoiceType.exe is open: ask me to close it (tray icon > خروج), or close it yourself with `taskkill /IM VoiceType.exe`, then start it again afterwards.

## Common tasks
**Models on this computer (free, offline) — the main option**
```
voicetype-cli.exe local list
voicetype-cli.exe local download ggml-large-v3-turbo-q5_0.bin
voicetype-cli.exe local gpu                                  # GPU acceleration (Vulkan), then restart VoiceType.exe
voicetype-cli.exe local download https://huggingface.co/<user>/<repo>/resolve/main/<model>.bin
voicetype-cli.exe local add-file <path-to-model.bin>
```
Local models must be whisper.cpp / ggml `.bin` files. When I ask for a better Arabic or Egyptian model, search Hugging Face for whisper.cpp ggml `.bin` files fine-tuned on Arabic/Egyptian speech, tell me the size, and download it with `local download <url>`.

**Choose which model the main program uses**
```
voicetype-cli.exe app show
voicetype-cli.exe app primary gemini gemini-3.5-transcribe        # dictation model
voicetype-cli.exe app fallback local ggml-large-v3-turbo-q5_0.bin # used automatically if the primary fails
voicetype-cli.exe app files gemini gemini-3.5-transcribe          # audio-file transcription
```
Recommended free setup: primary = an online free-tier model with a key (best accuracy), fallback = a local model (works offline).

**Save and test a key**
```
voicetype-cli.exe keys set openrouter <KEY>
voicetype-cli.exe keys set cohere <KEY>
voicetype-cli.exe keys list
voicetype-cli.exe keys add gemini <SECOND-ACCOUNT-KEY>   # extra keys rotate automatically when one hits its limit
```
Built-in provider ids: `gemini` (free tier), `groq` (free tier), `huggingface` (free tier), `cohere` (trial), `deepgram` (trial), `openrouter` / `openai` / `elevenlabs` (paid — do not use without my approval), `openai-compatible` (your own server).

**Find and add a model**
- OpenRouter speech-to-text models: https://openrouter.ai/collections/speech-to-text-models — use the exact model id shown on the model page (format `vendor/model`).
- Hugging Face: models with task "automatic-speech-recognition" (prefer ones trained on Arabic / Egyptian Arabic).
```
voicetype-cli.exe models add openrouter <vendor/model-id>
voicetype-cli.exe models add huggingface <user/model-id>
voicetype-cli.exe models disable <provider> <model|all>
voicetype-cli.exe models enable <provider> <model|all>
voicetype-cli.exe models remove <provider> <model>
```
To check that a new model id is valid without a full run: disable the others, run `compare run` on the recordings, look for FAIL lines (NotFound / BadRequest = wrong id), then re-enable the others.

**Add a completely new provider (any HTTP API)**
1. Read the provider's official API docs (endpoint, auth header, request format, where the text is in the JSON response).
2. Write a definition JSON following `docs\custom-provider.md` (use the built-in definitions as examples: `voicetype-cli.exe providers show openrouter`).
3. `voicetype-cli.exe providers add <file.json>` → `voicetype-cli.exe keys set <id> <KEY>` → `voicetype-cli.exe compare preview` to inspect the request (keys are hidden).

**Local / self-hosted server with an OpenAI-compatible API**
```
voicetype-cli.exe models add openai-compatible <model> --var base_url=http://localhost:8000/v1
```

**Run the comparison**
```
voicetype-cli.exe samples list
voicetype-cli.exe settings language ar
voicetype-cli.exe compare run --open
```
Then summarize for me in Arabic: the ranking, the best model, word error rate, how well English words were written in English letters, and any failures with their likely cause.

## Full command list
```
voicetype-cli app show | primary <provider> <model> | fallback <provider> <model> | files <provider> <model>
voicetype-cli providers list [--json] | show <id> | add <file.json> | remove <id>
voicetype-cli keys list [--json] | set <provider> <key> [--no-test] | test <provider> | remove <provider>
voicetype-cli models list [--json] | add <provider> <model> [--var name=value] | remove <provider> <model>
voicetype-cli models enable|disable <provider> <model|all>
voicetype-cli settings show | language <ar|en|auto> | audio-folder <path|default>
voicetype-cli local list | download <file|https-url> | add-file <model.bin> | remove <file> | gpu
voicetype-cli samples list [--audio <folder>]
voicetype-cli compare preview | run [--audio <folder>] [--open]
voicetype-cli paths
```
