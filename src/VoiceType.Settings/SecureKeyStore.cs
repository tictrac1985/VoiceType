using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceType.Settings;

/// <summary>
/// مخزن مفاتيح API مشفر بـ Windows DPAPI (مرتبط بحساب المستخدم على هذا الجهاز).
/// الملف نفسه لا يحتوي أي مفتاح مقروء، ولا يمكن فك تشفيره من حساب أو جهاز آخر.
/// </summary>
public sealed class SecureKeyStore
{
    private static readonly byte[] Entropy = "VoiceType.ApiKeys.v1"u8.ToArray();
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Func<string, string> _protect;
    private readonly Func<string, string?> _unprotect;
    private readonly bool _customProtector;
    private Dictionary<string, string> _protected;
    private readonly Dictionary<string, string> _shared = new(StringComparer.OrdinalIgnoreCase);

    // المفاتيح بعد فك تشفيرها (في الذاكرة فقط) حتى لا نفك التشفير مع كل إملاء
    private readonly Dictionary<string, string?> _plain = new(StringComparer.OrdinalIgnoreCase);

    public SecureKeyStore(string? filePath = null)
        : this(filePath ?? AppPaths.KeysFile, Protect, TryUnprotect, customProtector: false)
    {
    }

    /// <summary>للاختبارات: تشفير بديل بدل DPAPI.</summary>
    public SecureKeyStore(string filePath, Func<string, string> protect, Func<string, string?> unprotect)
        : this(filePath, protect, unprotect, customProtector: true)
    {
    }

    private SecureKeyStore(string filePath, Func<string, string> protect, Func<string, string?> unprotect, bool customProtector)
    {
        _filePath = filePath;
        _protect = protect;
        _unprotect = unprotect;
        _customProtector = customProtector;
        _protected = Load(_filePath);
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>مفاتيح موجودة في الملف لكن تعذّر فك تشفيرها (مثل نقل النسخة المحمولة لجهاز آخر).</summary>
    public IReadOnlyCollection<string> Unreadable
    {
        get
        {
            lock (_gate)
            {
                return _protected.Keys.Where(id => _unprotect(_protected[id]) is null).Select(BaseOf).Distinct().ToList();
            }
        }
    }

    public IReadOnlyCollection<string> ProviderIds
    {
        get
        {
            lock (_gate)
            {
                return _protected.Keys.Select(BaseOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// خدمتان بمفتاح واحد (مثل OpenRouter ونماذجه المجانية): مفاتيح providerId تُقرأ وتُحفظ باسم ownerId.
    /// </summary>
    public void ShareKey(string providerId, string ownerId)
    {
        lock (_gate)
        {
            _shared[providerId] = ownerId;
        }
    }

    private string Resolve(string providerId) => _shared.TryGetValue(providerId, out var owner) ? owner : providerId;

    /// <summary>اسم الخدمة من اسم الخانة: "gemini#2" ← "gemini".</summary>
    public static string BaseOf(string slot)
    {
        var hash = slot.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? slot : slot[..hash];
    }

    private static int SlotNumber(string slot)
    {
        var hash = slot.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? 1 : int.TryParse(slot[(hash + 1)..], out var n) ? n : int.MaxValue;
    }

    /// <summary>
    /// كل مفاتيح الخدمة بالترتيب (يمكن حفظ أكثر من مفتاح لنفس الخدمة، مثل عدة حسابات Gemini).
    /// المفاتيح التي لا يمكن فك تشفيرها لا تظهر.
    /// </summary>
    public IReadOnlyList<StoredKey> List(string providerId)
    {
        lock (_gate)
        {
            var owner = Resolve(providerId);
            return _protected
                .Where(entry => string.Equals(BaseOf(entry.Key), owner, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => SlotNumber(entry.Key))
                .Select(entry => (entry.Key, Value: Plain(entry.Key, entry.Value)))
                .Where(entry => entry.Value is not null)
                .Select(entry => new StoredKey(entry.Key, entry.Value!))
                .ToList();
        }
    }

    private string? Plain(string slot, string stored)
    {
        if (!_plain.TryGetValue(slot, out var value))
        {
            value = _unprotect(stored);
            _plain[slot] = value;
        }

        return value;
    }

    public IReadOnlyList<string> GetAll(string providerId) => List(providerId).Select(k => k.Value).ToList();

    public bool Has(string providerId) => List(providerId).Count > 0;

    /// <summary>أول مفتاح محفوظ للخدمة.</summary>
    public string? Get(string providerId) => List(providerId).FirstOrDefault()?.Value;

    /// <summary>يحفظ المفتاح الأول للخدمة (يستبدله). القيمة الفارغة تحذفه.</summary>
    public void Set(string providerId, string? apiKey)
    {
        EnsureSupported();
        lock (_gate)
        {
            providerId = Resolve(providerId);
            var cleaned = Clean(apiKey);
            if (cleaned is null)
            {
                _protected.Remove(providerId);
            }
            else
            {
                _protected[providerId] = _protect(cleaned);
            }

            Save();
        }
    }

    /// <summary>يضيف مفتاحًا جديدًا للخدمة بجانب المفاتيح الموجودة. يرجع اسم خانته. المفتاح المكرر لا يُضاف مرتين.</summary>
    public string Add(string providerId, string apiKey)
    {
        EnsureSupported();
        var cleaned = Clean(apiKey) ?? throw new ArgumentException("Empty key.", nameof(apiKey));
        lock (_gate)
        {
            var owner = Resolve(providerId);
            var existing = List(owner).FirstOrDefault(k => k.Value == cleaned);
            if (existing is not null)
            {
                return existing.Slot;
            }

            var slot = owner;
            for (var n = 2; _protected.ContainsKey(slot); n++)
            {
                slot = $"{owner}#{n}";
            }

            _protected[slot] = _protect(cleaned);
            Save();
            return slot;
        }
    }

    /// <summary>يحذف مفتاحًا واحدًا بخانته.</summary>
    public void RemoveSlot(string slot)
    {
        lock (_gate)
        {
            if (_protected.Remove(slot))
            {
                Save();
            }
        }
    }

    /// <summary>يحذف كل مفاتيح الخدمة.</summary>
    public void Remove(string providerId)
    {
        lock (_gate)
        {
            var owner = Resolve(providerId);
            var slots = _protected.Keys.Where(k => string.Equals(BaseOf(k), owner, StringComparison.OrdinalIgnoreCase)).ToList();
            slots.ForEach(k => _protected.Remove(k));
            if (slots.Count > 0)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// ينظف المفتاح الملصوق: يحذف المسافات والأسطر وعلامات التنصيص، وكلمة Bearer إن لُصقت معه.
    /// </summary>
    public static string? Clean(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var value = apiKey.Trim().Trim('"', '\'', '`').Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..].Trim();
        }

        value = new string(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return value.Length == 0 ? null : value;
    }

    private void Save()
    {
        _plain.Clear();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(new KeyFile { Version = 1, Keys = _protected }, new JsonSerializerOptions { WriteIndented = true });
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
        File.Move(temp, _filePath, overwrite: true);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path));
                if (file?.Keys is { } keys)
                {
                    return new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // ملف تالف أو غير قابل للقراءة: نبدأ بمخزن فارغ بدل أن يتعطل البرنامج
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure key storage requires Windows.");
        }

        return Convert.ToBase64String(ProtectWindows(Encoding.UTF8.GetBytes(plain)));
    }

    private static string? TryUnprotect(string stored)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(UnprotectWindows(Convert.FromBase64String(stored)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private void EnsureSupported()
    {
        if (!_customProtector && !IsSupported)
        {
            throw new PlatformNotSupportedException("Secure key storage requires Windows.");
        }
    }

    private sealed class KeyFile
    {
        public int Version { get; set; }

        public Dictionary<string, string> Keys { get; set; } = [];
    }
}

/// <summary>مفتاح محفوظ: الخانة (مثل gemini#2) والقيمة.</summary>
public sealed record StoredKey(string Slot, string Value);
