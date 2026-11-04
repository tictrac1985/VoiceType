using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceType.CrispHost;

/// <summary>
/// المساعد: يحمّل نموذج التفريغ مرة واحدة (لو اتحدد)، وبعدها ينفّذ كل طلب يوصله.
///
/// التشغيل:  VoiceType.CrispHost --lib &lt;مجلد crispasr.dll&gt; [--model &lt;ملف.gguf&gt;] [--threads N] [--gpu 1|0]
///           من غير --model: بيشتغل لمعرفة المتكلمين بس.
///
/// الرسائل (كل رسالة سطر واحد يبدأ بـ "@@VT " عشان أي كلام تطبعه المكتبة نفسها ميلخبطش الردود):
///   عند البداية:   {"ready":true,"backend":"cohere","ms":1234}   أو   {"fatal":"..."}
///   تفريغ:         {"id":1,"pcm":"...\\chunk.f32","lang":"ar","beam":0}
///                  ← {"id":1,"segments":[{"t0":0,"t1":250,"text":"...","words":[{"t0":0,"t1":40,"text":"..."}]}],"ms":900}
///   مين بيتكلم:    {"id":2,"op":"diarize","pcm":"...\\all.f32","segments":[[t0,t1],...],"embedder":"...gguf","num":0,"max":6}
///                  ← {"id":2,"speakers":[0,1,0,...],"ms":800}
///   (الأوقات كلها بالـ 1/100 ثانية، والصوت float32، 16kHz، قناة واحدة)
///   أي خطأ:        {"id":1,"error":"..."}
/// ينتهي البرنامج لما صدى يقفل stdin.
/// </summary>
internal static unsafe class Program
{
    private const string Marker = "@@VT ";

    // لما نموذج التفريغ يكون مفتوح، المجلد الحالي ممكن يكون متغير عشانه، فمنغيّروش تاني
    private static bool _modelOpen;

    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        var libDir = Arg(args, "--lib") ?? AppContext.BaseDirectory;
        var model = Arg(args, "--model");
        var threads = int.TryParse(Arg(args, "--threads"), out var t) && t > 0 ? t : Math.Clamp(Environment.ProcessorCount - 2, 1, 16);
        var useGpu = Arg(args, "--gpu") != "0";
        if (model is not null && !File.Exists(model))
        {
            Send(stdout, new JsonObject { ["fatal"] = "ملف النموذج مش موجود: " + model });
            return 2;
        }

        Native native;
        try
        {
            native = Native.Load(libDir);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
        {
            Send(stdout, new JsonObject { ["fatal"] = "تعذّر تحميل مكتبة CrispASR: " + ex.Message });
            return 3;
        }

        var clock = Stopwatch.StartNew();
        nint session = 0;
        if (model is not null)
        {
            session = native.Open(NativePath(model, allowChangeDirectory: true), threads, useGpu);
            if (session == 0)
            {
                Send(stdout, new JsonObject { ["fatal"] = "تعذّر فتح النموذج. المكتبة تدعم: " + native.AvailableBackends() });
                return 4;
            }

            _modelOpen = true;
        }

        Send(stdout, new JsonObject { ["ready"] = true, ["backend"] = session == 0 ? "diarize" : native.Backend(session), ["ms"] = clock.ElapsedMilliseconds });

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? id = null;
            try
            {
                var request = JsonNode.Parse(line) ?? throw new FormatException("طلب فاضي.");
                id = request["id"]?.DeepClone();
                var pcmPath = (string?)request["pcm"] ?? throw new FormatException("الطلب ناقصه مسار الصوت.");
                var samples = ReadFloats(pcmPath);
                clock.Restart();
                if ((string?)request["op"] == "diarize")
                {
                    var speakers = native.Diarize(samples, request["segments"]?.AsArray() ?? [],
                        (string?)request["embedder"] ?? throw new FormatException("ناقص نموذج الأصوات."),
                        (int?)request["num"] ?? 0, (int?)request["max"] ?? 0, threads);
                    Send(stdout, new JsonObject { ["id"] = id, ["speakers"] = speakers, ["ms"] = clock.ElapsedMilliseconds });
                    continue;
                }

                if (session == 0)
                {
                    throw new InvalidOperationException("المساعد شغال من غير نموذج تفريغ.");
                }

                if ((int?)request["beam"] is > 0 and var beam)
                {
                    native.SetBeam(session, beam);
                }

                var lang = (string?)request["lang"];
                var segments = native.Transcribe(session, samples, string.IsNullOrWhiteSpace(lang) ? "ar" : lang);
                Send(stdout, new JsonObject { ["id"] = id, ["segments"] = segments, ["ms"] = clock.ElapsedMilliseconds });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Send(stdout, new JsonObject { ["id"] = id, ["error"] = ex.Message });
            }
        }

        if (session != 0)
        {
            native.Close(session);
        }

        return 0;
    }

    /// <summary>
    /// مكتبة CrispASR بتفتح الملفات بـ fopen. على ويندوز fopen مبيفهمش المسارات اللي فيها حروف عربي
    /// (زي «مساعد الإملاء»)، فبنديها مسار بحروف إنجليزي بس:
    /// 1) المسار زي ما هو لو كله إنجليزي، 2) الاسم المختصر 8.3 لو ويندوز عامله،
    /// 3) اسم الملف بس بعد ما نخلي مجلده هو المجلد الحالي، 4) نسخة في مجلد مؤقت لو الملف صغير (زي نموذج بصمة الأصوات).
    /// </summary>
    internal static string NativePath(string path, bool allowChangeDirectory)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || IsAscii(full))
        {
            return full;
        }

        var buffer = new char[1024];
        var length = GetShortPathNameW(full, buffer, buffer.Length);
        if (length > 0 && length < buffer.Length && IsAscii(new string(buffer, 0, (int)length)))
        {
            return new string(buffer, 0, (int)length);
        }

        var folder = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        if (IsAscii(name))
        {
            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), full);
            if (IsAscii(relative))
            {
                return relative;
            }

            if (allowChangeDirectory)
            {
                Directory.SetCurrentDirectory(folder);
                return name;
            }
        }

        var size = new FileInfo(full).Length;
        var temp = Path.Combine(Path.GetTempPath(), "voicetype-crisp");
        if (size <= 300L * 1024 * 1024 && IsAscii(temp))
        {
            Directory.CreateDirectory(temp);
            var copy = Path.Combine(temp, $"{size}-{(IsAscii(name) ? name : "model" + Path.GetExtension(full))}");
            if (!File.Exists(copy) || new FileInfo(copy).Length != size)
            {
                File.Copy(full, copy, overwrite: true);
            }

            return copy;
        }

        throw new IOException("مكتبة CrispASR مش بتقدر تفتح ملفات في مسار فيه حروف عربي: " + full);
    }

    private static bool IsAscii(string value) => value.All(c => c < 128);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, int bufferLength);

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static float[] ReadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    private static void Send(StreamWriter stdout, JsonObject message) =>
        stdout.WriteLine(Marker + message.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    /// <summary>دوال مكتبة CrispASR (نفس الواجهة اللي بتستخدمها نسخة Python الرسمية).</summary>
    private sealed class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct OpenParams
        {
            public int AbiVersion;
            public int Threads;
            public int UseGpu;
            public int Verbosity;
            public int FlashAttention;
            public int GpuLayers;
            public fixed int Reserved[6];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiarizeSegment
        {
            public long T0;
            public long T1;
            public int Speaker;
            public int Padding;
        }

        /// <summary>نفس ترتيب crispasr_diarize_opts_abi بالظبط (48 بايت على 64-bit).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DiarizeOptions
        {
            public int Method;
            public int Threads;
            public long SliceT0;
            public byte* PyannoteModel;
            public byte* EmbedderModel;
            public int MinSpeakers;
            public int MaxSpeakers;
            public int NumSpeakers;
            public int Padding;
        }

        /// <summary>FoxNose: بصمة صوت WeSpeaker + تجميع (الأفضل في تجربتنا).</summary>
        private const int FoxNose = 4;

        private delegate* unmanaged[Cdecl]<byte*, int, nint> _open;
        private delegate* unmanaged[Cdecl]<byte*, byte*, OpenParams*, nint> _openWithParams;
        private delegate* unmanaged[Cdecl]<nint, byte*> _backend;
        private delegate* unmanaged[Cdecl]<byte*, int, int> _availableBackends;
        private delegate* unmanaged[Cdecl]<nint, float*, int, nint> _transcribe;
        private delegate* unmanaged[Cdecl]<nint, float*, int, byte*, nint> _transcribeLang;
        private delegate* unmanaged[Cdecl]<nint, int> _segments;
        private delegate* unmanaged[Cdecl]<nint, int, byte*> _segmentText;
        private delegate* unmanaged[Cdecl]<nint, int, long> _segmentT0;
        private delegate* unmanaged[Cdecl]<nint, int, long> _segmentT1;
        private delegate* unmanaged[Cdecl]<nint, int, int> _words;
        private delegate* unmanaged[Cdecl]<nint, int, int, byte*> _wordText;
        private delegate* unmanaged[Cdecl]<nint, int, int, long> _wordT0;
        private delegate* unmanaged[Cdecl]<nint, int, int, long> _wordT1;
        private delegate* unmanaged[Cdecl]<nint, void> _resultFree;
        private delegate* unmanaged[Cdecl]<nint, void> _close;
        private delegate* unmanaged[Cdecl]<nint, int, int> _setBeam;
        private delegate* unmanaged[Cdecl]<float*, float*, int, int, DiarizeSegment*, int, DiarizeOptions*, int> _diarize;

        public static Native Load(string directory)
        {
            var file = OperatingSystem.IsWindows() ? "crispasr.dll" : "libcrispasr.so";
            var path = Path.Combine(directory, file);
            if (OperatingSystem.IsWindows())
            {
                // ملفات ggml و whisper اللي جنب المكتبة تتحمّل من نفس المجلد
                SetDllDirectoryW(directory);
            }

            var lib = NativeLibrary.Load(path);
            var n = new Native
            {
                _open = (delegate* unmanaged[Cdecl]<byte*, int, nint>)Get(lib, "crispasr_session_open"),
                _backend = (delegate* unmanaged[Cdecl]<nint, byte*>)Get(lib, "crispasr_session_backend"),
                _availableBackends = (delegate* unmanaged[Cdecl]<byte*, int, int>)Get(lib, "crispasr_session_available_backends"),
                _transcribe = (delegate* unmanaged[Cdecl]<nint, float*, int, nint>)Get(lib, "crispasr_session_transcribe"),
                _segments = (delegate* unmanaged[Cdecl]<nint, int>)Get(lib, "crispasr_session_result_n_segments"),
                _segmentText = (delegate* unmanaged[Cdecl]<nint, int, byte*>)Get(lib, "crispasr_session_result_segment_text"),
                _segmentT0 = (delegate* unmanaged[Cdecl]<nint, int, long>)Get(lib, "crispasr_session_result_segment_t0"),
                _segmentT1 = (delegate* unmanaged[Cdecl]<nint, int, long>)Get(lib, "crispasr_session_result_segment_t1"),
                _resultFree = (delegate* unmanaged[Cdecl]<nint, void>)Get(lib, "crispasr_session_result_free"),
                _close = (delegate* unmanaged[Cdecl]<nint, void>)Get(lib, "crispasr_session_close"),
            };

            // اختيارية (موجودة في النسخ الأحدث)
            if (NativeLibrary.TryGetExport(lib, "crispasr_session_open_with_params", out var withParams))
            {
                n._openWithParams = (delegate* unmanaged[Cdecl]<byte*, byte*, OpenParams*, nint>)withParams;
            }

            if (NativeLibrary.TryGetExport(lib, "crispasr_session_transcribe_lang", out var lang))
            {
                n._transcribeLang = (delegate* unmanaged[Cdecl]<nint, float*, int, byte*, nint>)lang;
            }

            if (NativeLibrary.TryGetExport(lib, "crispasr_session_result_n_words", out var words) &&
                NativeLibrary.TryGetExport(lib, "crispasr_session_result_word_text", out var wordText) &&
                NativeLibrary.TryGetExport(lib, "crispasr_session_result_word_t0", out var wordT0) &&
                NativeLibrary.TryGetExport(lib, "crispasr_session_result_word_t1", out var wordT1))
            {
                n._words = (delegate* unmanaged[Cdecl]<nint, int, int>)words;
                n._wordText = (delegate* unmanaged[Cdecl]<nint, int, int, byte*>)wordText;
                n._wordT0 = (delegate* unmanaged[Cdecl]<nint, int, int, long>)wordT0;
                n._wordT1 = (delegate* unmanaged[Cdecl]<nint, int, int, long>)wordT1;
            }

            if (NativeLibrary.TryGetExport(lib, "crispasr_session_set_beam_size", out var beam))
            {
                n._setBeam = (delegate* unmanaged[Cdecl]<nint, int, int>)beam;
            }

            if (NativeLibrary.TryGetExport(lib, "crispasr_diarize_segments_abi", out var diarize))
            {
                n._diarize = (delegate* unmanaged[Cdecl]<float*, float*, int, int, DiarizeSegment*, int, DiarizeOptions*, int>)diarize;
            }

            return n;
        }

        public nint Open(string model, int threads, bool useGpu)
        {
            var path = Utf8(model);
            fixed (byte* p = path)
            {
                if (_openWithParams != null)
                {
                    var options = new OpenParams
                    {
                        AbiVersion = 2,
                        Threads = threads,
                        UseGpu = useGpu ? 1 : 0,
                        Verbosity = 0,
                        FlashAttention = 1,
                        GpuLayers = -1,
                    };
                    var handle = _openWithParams(p, null, &options);
                    if (handle != 0 || !useGpu)
                    {
                        return handle;
                    }

                    // لو كرت الشاشة رفض، نجرّب على المعالج
                    options.UseGpu = 0;
                    return _openWithParams(p, null, &options);
                }

                return _open(p, threads);
            }
        }

        public string Backend(nint session) => Marshal.PtrToStringUTF8((nint)_backend(session)) ?? string.Empty;

        public void SetBeam(nint session, int size)
        {
            if (_setBeam != null)
            {
                _setBeam(session, size);
            }
        }

        public string AvailableBackends()
        {
            var buffer = new byte[1024];
            fixed (byte* b = buffer)
            {
                var length = _availableBackends(b, buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, Math.Clamp(length, 0, buffer.Length)).TrimEnd('\0');
            }
        }

        public JsonArray Transcribe(nint session, float[] samples, string language)
        {
            nint result;
            fixed (float* s = samples)
            {
                if (_transcribeLang != null)
                {
                    var lang = Utf8(language);
                    fixed (byte* l = lang)
                    {
                        result = _transcribeLang(session, s, samples.Length, l);
                    }
                }
                else
                {
                    result = _transcribe(session, s, samples.Length);
                }
            }

            if (result == 0)
            {
                throw new InvalidOperationException("النموذج مرجّعش نتيجة.");
            }

            try
            {
                var segments = new JsonArray();
                var count = _segments(result);
                for (var i = 0; i < count; i++)
                {
                    var segment = new JsonObject
                    {
                        ["t0"] = _segmentT0(result, i),
                        ["t1"] = _segmentT1(result, i),
                        ["text"] = Marshal.PtrToStringUTF8((nint)_segmentText(result, i)) ?? string.Empty,
                    };
                    if (_words != null)
                    {
                        var words = new JsonArray();
                        var n = _words(result, i);
                        for (var j = 0; j < n; j++)
                        {
                            words.Add(new JsonObject
                            {
                                ["t0"] = _wordT0(result, i, j),
                                ["t1"] = _wordT1(result, i, j),
                                ["text"] = Marshal.PtrToStringUTF8((nint)_wordText(result, i, j)) ?? string.Empty,
                            });
                        }

                        segment["words"] = words;
                    }

                    segments.Add(segment);
                }

                return segments;
            }
            finally
            {
                _resultFree(result);
            }
        }

        /// <summary>يحدد مين اللي قال كل جزء (0، 1، 2…؛ و -1 لو مش عارف).</summary>
        public JsonArray Diarize(float[] samples, JsonArray ranges, string embedder, int numSpeakers, int maxSpeakers, int threads)
        {
            if (_diarize == null)
            {
                throw new InvalidOperationException("نسخة CrispASR دي مفيهاش معرفة المتكلمين.");
            }

            if (!File.Exists(embedder))
            {
                throw new FileNotFoundException("نموذج بصمة الأصوات مش موجود: " + embedder);
            }

            var segments = new DiarizeSegment[ranges.Count];
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = new DiarizeSegment { T0 = (long?)ranges[i]?[0] ?? 0, T1 = (long?)ranges[i]?[1] ?? 0, Speaker = -1 };
            }

            var embedderPath = Utf8(NativePath(embedder, allowChangeDirectory: !_modelOpen));
            int rc;
            fixed (float* pcm = samples)
            fixed (DiarizeSegment* segs = segments)
            fixed (byte* e = embedderPath)
            {
                var options = new DiarizeOptions
                {
                    Method = FoxNose,
                    Threads = threads,
                    EmbedderModel = e,
                    NumSpeakers = Math.Max(0, numSpeakers),
                    MaxSpeakers = Math.Max(0, maxSpeakers),
                };
                rc = segments.Length == 0 ? 0 : _diarize(pcm, pcm, samples.Length, 0, segs, segments.Length, &options);
            }

            if (rc != 0)
            {
                throw new InvalidOperationException($"معرفة المتكلمين فشلت (rc={rc}).");
            }

            var speakers = new JsonArray();
            foreach (var segment in segments)
            {
                speakers.Add(segment.Speaker);
            }

            return speakers;
        }

        public void Close(nint session) => _close(session);

        private static nint Get(nint lib, string name) => NativeLibrary.GetExport(lib, name);

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);
}
