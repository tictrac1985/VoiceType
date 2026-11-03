using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceType.CrispHost;

/// <summary>
/// المساعد: يحمّل النموذج مرة واحدة، وبعدها يفرّغ كل طلب يوصله.
///
/// التشغيل:  VoiceType.CrispHost --lib &lt;مجلد crispasr.dll&gt; --model &lt;ملف.gguf&gt; [--threads N] [--gpu 1|0]
///
/// الرسائل (كل رسالة سطر واحد يبدأ بـ "@@VT " عشان أي كلام تطبعه المكتبة نفسها ميلخبطش الردود):
///   عند البداية:   {"ready":true,"backend":"cohere","ms":1234}   أو   {"fatal":"..."}
///   طلب (stdin):   {"id":1,"pcm":"C:\\...\\chunk.f32","lang":"ar"}   (الملف: أرقام float32، 16kHz، قناة واحدة)
///   الرد:          {"id":1,"segments":[{"t0":0,"t1":250,"text":"..."}],"ms":900}   (t0/t1 بالـ 1/100 ثانية)
///                  أو {"id":1,"error":"..."}
/// ينتهي البرنامج لما صدى يقفل stdin.
/// </summary>
internal static unsafe class Program
{
    private const string Marker = "@@VT ";

    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        var libDir = Arg(args, "--lib") ?? AppContext.BaseDirectory;
        var model = Arg(args, "--model");
        var threads = int.TryParse(Arg(args, "--threads"), out var t) && t > 0 ? t : Math.Clamp(Environment.ProcessorCount - 2, 1, 16);
        var useGpu = Arg(args, "--gpu") != "0";
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
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
        var session = native.Open(model, threads, useGpu);
        if (session == 0)
        {
            Send(stdout, new JsonObject { ["fatal"] = "تعذّر فتح النموذج. المكتبة تدعم: " + native.AvailableBackends() });
            return 4;
        }

        Send(stdout, new JsonObject { ["ready"] = true, ["backend"] = native.Backend(session), ["ms"] = clock.ElapsedMilliseconds });

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
                var lang = (string?)request["lang"];
                var samples = ReadFloats(pcmPath);
                clock.Restart();
                var segments = native.Transcribe(session, samples, string.IsNullOrWhiteSpace(lang) ? "ar" : lang);
                Send(stdout, new JsonObject { ["id"] = id, ["segments"] = segments, ["ms"] = clock.ElapsedMilliseconds });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Send(stdout, new JsonObject { ["id"] = id, ["error"] = ex.Message });
            }
        }

        native.Close(session);
        return 0;
    }

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
        private delegate* unmanaged[Cdecl]<nint, void> _resultFree;
        private delegate* unmanaged[Cdecl]<nint, void> _close;

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
                    segments.Add(new JsonObject
                    {
                        ["t0"] = _segmentT0(result, i),
                        ["t1"] = _segmentT1(result, i),
                        ["text"] = Marshal.PtrToStringUTF8((nint)_segmentText(result, i)) ?? string.Empty,
                    });
                }

                return segments;
            }
            finally
            {
                _resultFree(result);
            }
        }

        public void Close(nint session) => _close(session);

        private static nint Get(nint lib, string name) => NativeLibrary.GetExport(lib, name);

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);
}
