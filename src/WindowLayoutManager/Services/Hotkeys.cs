using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowLayoutManager.Services;

/// <summary>The ten quick-apply actions. Numeric order is also the on-disk order.</summary>
public enum HotkeyActionId
{
    Slot1 = 0, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8,
    Previous,
    Next
}

public static class HotkeyActions
{
    public const int SlotCount = 8;
    public static readonly IReadOnlyList<HotkeyActionId> All = Enum.GetValues<HotkeyActionId>();

    public static bool IsSlot(HotkeyActionId a) => a >= HotkeyActionId.Slot1 && a <= HotkeyActionId.Slot8;

    /// <summary>1..8 for slot actions.</summary>
    public static int SlotNumber(HotkeyActionId a) => (int)a + 1;

    public static HotkeyActionId FromSlotNumber(int n)
    {
        if (n < 1 || n > SlotCount) throw new ArgumentOutOfRangeException(nameof(n));
        return (HotkeyActionId)(n - 1);
    }

    public static string JsonName(HotkeyActionId a) => a switch
    {
        HotkeyActionId.Previous => "previous",
        HotkeyActionId.Next => "next",
        _ => "slot" + SlotNumber(a)
    };

    public static bool TryParseJsonName(string? s, out HotkeyActionId action)
    {
        foreach (var a in All)
        {
            if (string.Equals(JsonName(a), s, StringComparison.Ordinal)) { action = a; return true; }
        }
        action = default;
        return false;
    }

    public static string KoreanLabel(HotkeyActionId a) => a switch
    {
        HotkeyActionId.Previous => "이전 배치",
        HotkeyActionId.Next => "다음 배치",
        _ => $"슬롯 {SlotNumber(a)}"
    };

    /// <summary>RegisterHotKey id (1..10). Ids are scoped to our own message-only window.</summary>
    public static int RegistrationId(HotkeyActionId a) => (int)a + 1;

    public static bool TryFromRegistrationId(int id, out HotkeyActionId action)
    {
        if (id >= 1 && id <= All.Count) { action = (HotkeyActionId)(id - 1); return true; }
        action = default;
        return false;
    }
}

/// <summary>Key/modifier rules shared by the settings dialog, the store validator and registration.</summary>
public static class HotkeyRules
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    private static readonly Dictionary<string, Keys> ByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Keys, (string name, string display)> ByKey = new();

    static HotkeyRules()
    {
        void Add(string name, Keys key, string display)
        {
            ByName[name] = key;
            ByKey[key] = (name, display);
        }
        for (char c = 'A'; c <= 'Z'; c++) Add(c.ToString(), (Keys)c, c.ToString());
        for (int d = 0; d <= 9; d++) Add($"D{d}", Keys.D0 + d, d.ToString());
        for (int f = 1; f <= 24; f++)
        {
            if (f == 12) continue; // F12 is reserved for the debugger by RegisterHotKey.
            Add($"F{f}", Keys.F1 + (f - 1), $"F{f}");
        }
        for (int n = 0; n <= 9; n++) Add($"NumPad{n}", Keys.NumPad0 + n, $"Num {n}");
        Add("Left", Keys.Left, "←");
        Add("Right", Keys.Right, "→");
        Add("Up", Keys.Up, "↑");
        Add("Down", Keys.Down, "↓");
        Add("Home", Keys.Home, "Home");
        Add("End", Keys.End, "End");
        Add("PageUp", Keys.PageUp, "PgUp");
        Add("PageDown", Keys.PageDown, "PgDn");
        Add("Insert", Keys.Insert, "Ins");
        Add("Delete", Keys.Delete, "Del");
        Add("OemMinus", Keys.OemMinus, "-");
        Add("Oemplus", Keys.Oemplus, "=");
        Add("OemOpenBrackets", Keys.OemOpenBrackets, "[");
        Add("OemCloseBrackets", Keys.OemCloseBrackets, "]");
        Add("OemSemicolon", Keys.OemSemicolon, ";");
        Add("OemQuotes", Keys.OemQuotes, "'");
        Add("Oemcomma", Keys.Oemcomma, ",");
        Add("OemPeriod", Keys.OemPeriod, ".");
        Add("OemQuestion", Keys.OemQuestion, "/");
        Add("Oemtilde", Keys.Oemtilde, "`");
        Add("OemPipe", Keys.OemPipe, "\\");
    }

    public static bool IsAllowedKey(Keys key) => ByKey.ContainsKey(key);

    /// <summary>Canonical on-disk key name. Only allowed keys have a name.</summary>
    public static string KeyName(Keys key) =>
        ByKey.TryGetValue(key, out var v) ? v.name : throw new ArgumentOutOfRangeException(nameof(key));

    /// <summary>Strict reverse lookup: only canonical names (no numeric values, no flag combinations).</summary>
    public static bool TryKeyFromName(string? name, out Keys key)
    {
        if (name != null && ByName.TryGetValue(name, out key)) return true;
        key = Keys.None;
        return false;
    }

    public static string KeyDisplay(Keys key) => ByKey.TryGetValue(key, out var v) ? v.display : key.ToString();

    public static string Format(bool ctrl, bool alt, bool shift, Keys key)
    {
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        sb.Append(KeyDisplay(key));
        return sb.ToString();
    }

    public static string Format(HotkeyBinding b) => Format(b.Ctrl, b.Alt, b.Shift, b.Key);

    /// <summary>Returns a Korean reason when the combination is not allowed, otherwise null.</summary>
    public static string? ValidateCombo(bool ctrl, bool alt, bool shift, Keys key)
    {
        if (key == Keys.F12) return "F12는 Windows가 디버거용으로 예약한 키라 사용할 수 없습니다.";
        if (key == Keys.LWin || key == Keys.RWin) return "Windows 키 조합은 사용할 수 없습니다.";
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey
            or Keys.LMenu or Keys.RMenu or Keys.None)
            return "Ctrl/Alt/Shift만으로는 단축키를 만들 수 없습니다. 다른 키를 함께 누르세요.";
        if (!IsAllowedKey(key)) return "이 키는 단축키로 사용할 수 없습니다.";
        if (!ctrl && !alt) return "Ctrl 또는 Alt 중 하나 이상을 함께 눌러야 합니다.";
        return null;
    }

    public static uint NativeModifiers(HotkeyBinding b) =>
        (b.Ctrl ? MOD_CONTROL : 0) | (b.Alt ? MOD_ALT : 0) | (b.Shift ? MOD_SHIFT : 0) | MOD_NOREPEAT;

    /// <summary>System.Windows.Forms.Keys values for the allowed keys are the Win32 virtual-key codes.</summary>
    public static uint VirtualKey(HotkeyBinding b) => (uint)b.Key;
}

public sealed class HotkeyBinding
{
    public HotkeyActionId Action { get; }
    /// <summary>Stable profile ID (slots only). Null = unassigned.</summary>
    public string? ProfileId { get; set; }
    public bool Enabled { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public Keys Key { get; set; }

    public HotkeyBinding(HotkeyActionId action) { Action = action; }

    public HotkeyBinding Clone() => new(Action)
    {
        ProfileId = ProfileId,
        Enabled = Enabled,
        Ctrl = Ctrl,
        Alt = Alt,
        Shift = Shift,
        Key = Key
    };

    public bool SameCombo(HotkeyBinding o) => Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Key == o.Key;

    public bool SameAs(HotkeyBinding o) =>
        Action == o.Action && string.Equals(ProfileId, o.ProfileId, StringComparison.Ordinal) && Enabled == o.Enabled && SameCombo(o);
}

public sealed class HotkeyConfig
{
    public const int CurrentVersion = 1;

    private readonly HotkeyBinding[] _bindings;

    private HotkeyConfig(HotkeyBinding[] bindings) { _bindings = bindings; }

    /// <summary>Exactly ten bindings, indexed by (int)HotkeyActionId.</summary>
    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public HotkeyBinding Get(HotkeyActionId a) => _bindings[(int)a];

    /// <summary>New installs and upgrades: every global key disabled, every slot unassigned.
    /// Default combos Ctrl+Alt+1..8 and Ctrl+Alt+Left/Right.</summary>
    public static HotkeyConfig CreateDefault()
    {
        var arr = new HotkeyBinding[HotkeyActions.All.Count];
        for (int i = 0; i < HotkeyActions.SlotCount; i++)
        {
            arr[i] = new HotkeyBinding((HotkeyActionId)i) { Enabled = false, Ctrl = true, Alt = true, Key = Keys.D1 + i };
        }
        arr[(int)HotkeyActionId.Previous] = new HotkeyBinding(HotkeyActionId.Previous) { Enabled = false, Ctrl = true, Alt = true, Key = Keys.Left };
        arr[(int)HotkeyActionId.Next] = new HotkeyBinding(HotkeyActionId.Next) { Enabled = false, Ctrl = true, Alt = true, Key = Keys.Right };
        return new HotkeyConfig(arr);
    }

    public static HotkeyBinding DefaultFor(HotkeyActionId a) => CreateDefault().Get(a);

    /// <summary>Caller guarantees one binding per action at its index.</summary>
    internal static HotkeyConfig FromBindings(HotkeyBinding[] ordered)
    {
        if (ordered.Length != HotkeyActions.All.Count) throw new ArgumentException("binding count");
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] is null || (int)ordered[i].Action != i) throw new ArgumentException("binding order");
        }
        return new HotkeyConfig(ordered);
    }

    public HotkeyConfig Clone() => new(_bindings.Select(b => b.Clone()).ToArray());

    public bool ContentEquals(HotkeyConfig other)
    {
        for (int i = 0; i < _bindings.Length; i++)
        {
            if (!_bindings[i].SameAs(other._bindings[i])) return false;
        }
        return true;
    }

    /// <summary>Korean validation errors (empty when valid).</summary>
    public List<string> Validate()
    {
        var errs = new List<string>();
        for (int i = 0; i < _bindings.Length; i++)
        {
            var b = _bindings[i];
            var label = HotkeyActions.KoreanLabel(b.Action);
            if ((int)b.Action != i) errs.Add($"{label}: 내부 순서 오류");
            var comboErr = HotkeyRules.ValidateCombo(b.Ctrl, b.Alt, b.Shift, b.Key);
            if (comboErr != null) errs.Add($"{label}: {comboErr}");
            if (HotkeyActions.IsSlot(b.Action))
            {
                if (b.ProfileId != null && !ProfileStore.IsSafeId(b.ProfileId))
                    errs.Add($"{label}: 배치 ID 형식이 올바르지 않습니다.");
            }
            else if (b.ProfileId != null)
            {
                errs.Add($"{label}: 이전/다음 항목에는 배치를 지정할 수 없습니다.");
            }
        }
        var enabled = _bindings.Where(b => b.Enabled).ToList();
        for (int i = 0; i < enabled.Count; i++)
        {
            for (int j = i + 1; j < enabled.Count; j++)
            {
                if (enabled[i].SameCombo(enabled[j]))
                {
                    errs.Add($"{HotkeyActions.KoreanLabel(enabled[i].Action)}와(과) {HotkeyActions.KoreanLabel(enabled[j].Action)}의 단축키가 같습니다 ({HotkeyRules.Format(enabled[i])}). 켜진 단축키는 서로 달라야 합니다.");
                }
            }
        }
        return errs;
    }
}

/// <summary>
/// Separate, versioned hotkeys.json in the existing app data directory. Independent of
/// settings.json / SettingsStore so MonitoringService persistence can never erase bindings.
/// Missing file → defaults (all disabled) WITHOUT writing anything. Corrupt/oversized file →
/// defaults + error, original bytes preserved as hotkeys.corrupt.&lt;ts&gt;.bad (deduplicated).
/// Save is validated + atomic (tmp + Replace); an invalid existing file is backed up first and a
/// failed backup aborts the save without touching the original.
/// </summary>
public class HotkeyStore
{
    public const string FileName = "hotkeys.json";
    public const long MaxFileBytes = 64 * 1024;

    private readonly string _dir;
    private readonly string _path;

    public string FilePath => _path;

    public HotkeyStore(string dataDir)
    {
        _dir = dataDir;
        _path = Path.Combine(dataDir, FileName);
    }

    public HotkeyConfig Load(out string? error)
    {
        error = null;
        if (!File.Exists(_path)) return HotkeyConfig.CreateDefault();
        byte[] bytes;
        try
        {
            var len = new FileInfo(_path).Length;
            if (len > MaxFileBytes)
            {
                PreserveCorrupt(null);
                error = $"{FileName} 파일이 너무 큽니다 (최대 {MaxFileBytes / 1024}KB).";
                return HotkeyConfig.CreateDefault();
            }
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception ex)
        {
            // Locked/unreadable: do not copy, do not overwrite. Start with safe defaults.
            error = $"{FileName} 파일을 읽지 못했습니다 ({ex.GetType().Name}).";
            return HotkeyConfig.CreateDefault();
        }
        try
        {
            return ParseAndValidate(bytes);
        }
        catch (InvalidDataException ex)
        {
            PreserveCorrupt(bytes);
            error = $"{FileName} 형식 오류: {ex.Message}";
            return HotkeyConfig.CreateDefault();
        }
        catch (Exception ex)
        {
            // Defensive: no malformed input may crash startup. Original stays untouched.
            PreserveCorrupt(bytes);
            error = $"{FileName} 형식 오류 ({ex.GetType().Name}).";
            return HotkeyConfig.CreateDefault();
        }
    }

    public virtual void Save(HotkeyConfig cfg)
    {
        var errs = cfg.Validate();
        if (errs.Count > 0) throw new InvalidDataException(string.Join(" / ", errs));
        var json = Serialize(cfg);
        Directory.CreateDirectory(_dir);
        if (File.Exists(_path) && !ExistingFileIsValid())
        {
            // Throws if the backup cannot be made → original stays untouched.
            File.Copy(_path, UniquePath("hotkeys.preoverwrite"), overwrite: false);
        }
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private bool ExistingFileIsValid()
    {
        try
        {
            if (new FileInfo(_path).Length > MaxFileBytes) return false;
            ParseAndValidate(File.ReadAllBytes(_path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PreserveCorrupt(byte[]? knownBytes)
    {
        try
        {
            byte[]? bytes = knownBytes;
            if (bytes is null && new FileInfo(_path).Length <= 16L * 1024 * 1024) bytes = File.ReadAllBytes(_path);
            if (bytes != null)
            {
                var hash = SHA256.HashData(bytes);
                foreach (var existing in Directory.EnumerateFiles(_dir, "hotkeys.corrupt.*.bad"))
                {
                    try
                    {
                        var fi = new FileInfo(existing);
                        if (fi.Length == bytes.LongLength && SHA256.HashData(File.ReadAllBytes(existing)).AsSpan().SequenceEqual(hash))
                            return; // identical bytes already preserved
                    }
                    catch { }
                }
            }
            File.Copy(_path, UniquePath("hotkeys.corrupt"), overwrite: false);
        }
        catch
        {
            // Best effort; the original file is never modified here.
        }
    }

    private string UniquePath(string prefix)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        for (int i = 0; i < 1000; i++)
        {
            var name = i == 0 ? $"{prefix}.{stamp}.bad" : $"{prefix}.{stamp}-{i}.bad";
            var p = Path.Combine(_dir, name);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
        throw new IOException("백업 파일 이름을 만들 수 없습니다.");
    }

    public static string Serialize(HotkeyConfig cfg)
    {
        var arr = new JsonArray();
        foreach (var b in cfg.Bindings)
        {
            var o = new JsonObject
            {
                ["action"] = HotkeyActions.JsonName(b.Action),
                ["enabled"] = b.Enabled,
                ["ctrl"] = b.Ctrl,
                ["alt"] = b.Alt,
                ["shift"] = b.Shift,
                ["key"] = HotkeyRules.KeyName(b.Key)
            };
            if (HotkeyActions.IsSlot(b.Action))
            {
                o["profileId"] = b.ProfileId is null ? null : JsonValue.Create(b.ProfileId);
            }
            arr.Add(o);
        }
        var root = new JsonObject
        {
            ["version"] = HotkeyConfig.CurrentVersion,
            ["bindings"] = arr
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static HotkeyConfig ParseAndValidate(byte[] bytes)
    {
        if (bytes.LongLength > MaxFileBytes) throw new InvalidDataException("파일이 너무 큽니다.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("UTF-8 텍스트가 아닙니다."); }
        return ParseAndValidate(text);
    }

    /// <summary>
    /// Every malformed input surfaces as InvalidDataException. System.Text.Json.Nodes materializes a
    /// JsonObject lazily, so a duplicate property name (e.g. two "version" or two "enabled" keys)
    /// throws ArgumentException only when the object is first accessed; that and other parser/shape
    /// exceptions are converted here so callers treat the file as corrupt instead of crashing.
    /// </summary>
    public static HotkeyConfig ParseAndValidate(string json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException
                                       or JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"JSON 구조 오류 (중복된 속성 이름 등): {ex.GetType().Name}");
        }
    }

    private static HotkeyConfig ParseCore(string json)
    {
        const char Bom = (char)0xFEFF;
        if (json.Length > 0 && json[0] == Bom) json = json[1..];
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new InvalidDataException("JSON 형식이 아닙니다."); }
        if (root is not JsonObject obj) throw new InvalidDataException("최상위가 객체가 아닙니다.");

        if (!obj.TryGetPropertyValue("version", out var vn) || vn is not JsonValue vv || !vv.TryGetValue<int>(out var ver))
            throw new InvalidDataException("version이 없거나 정수가 아닙니다.");
        if (ver != HotkeyConfig.CurrentVersion) throw new InvalidDataException($"지원하지 않는 버전입니다: {ver}");

        if (!obj.TryGetPropertyValue("bindings", out var bn) || bn is not JsonArray arr)
            throw new InvalidDataException("bindings 배열이 없습니다.");
        if (arr.Count != HotkeyActions.All.Count)
            throw new InvalidDataException($"bindings 항목은 정확히 {HotkeyActions.All.Count}개여야 합니다.");

        var found = new HotkeyBinding?[HotkeyActions.All.Count];
        foreach (var item in arr)
        {
            if (item is not JsonObject o) throw new InvalidDataException("bindings 항목이 객체가 아닙니다.");
            var actionName = RequireString(o, "action");
            if (!HotkeyActions.TryParseJsonName(actionName, out var action))
                throw new InvalidDataException($"알 수 없는 action: {Trunc(actionName)}");
            if (found[(int)action] != null) throw new InvalidDataException($"action이 중복되었습니다: {actionName}");

            var b = new HotkeyBinding(action)
            {
                Enabled = RequireBool(o, "enabled", actionName),
                Ctrl = RequireBool(o, "ctrl", actionName),
                Alt = RequireBool(o, "alt", actionName),
                Shift = RequireBool(o, "shift", actionName)
            };
            if (o.TryGetPropertyValue("win", out var wn) && wn is not null)
            {
                if (wn is not JsonValue wv || !wv.TryGetValue<bool>(out var win))
                    throw new InvalidDataException($"{actionName}: win 값 형식 오류");
                if (win) throw new InvalidDataException($"{actionName}: Windows 키 조합은 사용할 수 없습니다.");
            }
            var keyName = RequireString(o, "key");
            if (!HotkeyRules.TryKeyFromName(keyName, out var key))
                throw new InvalidDataException($"{actionName}: 사용할 수 없는 키 이름: {Trunc(keyName)}");
            b.Key = key;

            if (o.TryGetPropertyValue("profileId", out var pn) && pn is not null)
            {
                if (pn is not JsonValue pv || !pv.TryGetValue<string>(out var pid) || pid is null)
                    throw new InvalidDataException($"{actionName}: profileId 형식 오류");
                if (!HotkeyActions.IsSlot(action))
                    throw new InvalidDataException($"{actionName}에는 profileId를 지정할 수 없습니다.");
                if (!ProfileStore.IsSafeId(pid))
                    throw new InvalidDataException($"{actionName}: profileId가 안전하지 않습니다.");
                b.ProfileId = pid;
            }
            found[(int)action] = b;
        }

        var ordered = new HotkeyBinding[found.Length];
        for (int i = 0; i < found.Length; i++)
        {
            ordered[i] = found[i] ?? throw new InvalidDataException($"action 누락: {HotkeyActions.JsonName((HotkeyActionId)i)}");
        }
        var cfg = HotkeyConfig.FromBindings(ordered);
        var errs = cfg.Validate();
        if (errs.Count > 0) throw new InvalidDataException(string.Join(" / ", errs));
        return cfg;
    }

    private static string RequireString(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var n) || n is not JsonValue v || !v.TryGetValue<string>(out var s) || s is null)
            throw new InvalidDataException($"필수 문자열 누락/형식 오류: {name}");
        return s;
    }

    private static bool RequireBool(JsonObject o, string name, string actionName)
    {
        if (!o.TryGetPropertyValue(name, out var n) || n is not JsonValue v || !v.TryGetValue<bool>(out var b))
            throw new InvalidDataException($"{actionName}: 필수 true/false 값 누락/형식 오류: {name}");
        return b;
    }

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "…";
}

public enum HotkeyRegState { Disabled, Registered, Failed }

public sealed record HotkeyStatus(HotkeyRegState State, int Win32Error)
{
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    public static readonly HotkeyStatus Off = new(HotkeyRegState.Disabled, 0);

    public string KoreanText => State switch
    {
        HotkeyRegState.Disabled => "꺼짐",
        HotkeyRegState.Registered => "켜짐(등록됨)",
        _ when Win32Error == ERROR_HOTKEY_ALREADY_REGISTERED => "등록 실패: 다른 프로그램이 이미 사용 중",
        _ => $"등록 실패 (오류 {Win32Error})"
    };
}

/// <summary>Native registration seam. Production: GlobalHotkeyWindow. Self-tests: a fake.</summary>
public interface IHotkeyRegistrar
{
    bool TryRegister(int id, uint modifiers, uint virtualKey, out int win32Error);
    void Unregister(int id);
}

public enum HotkeyCommitOutcome { Applied, Invalid, SaveFailed }

public sealed record HotkeyCommitResult(
    HotkeyCommitOutcome Outcome,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<HotkeyActionId, HotkeyStatus> Statuses);

/// <summary>
/// Owns the in-memory hotkey config and the set of actually registered actions.
/// Commit order is SAVE FIRST, then re-register only changed actions. A validation or save
/// failure therefore leaves the previous in-memory config AND every previous registration
/// untouched (nothing to roll back). Registration collisions are reported per action as
/// Failed — never as enabled/registered — and never steal another program's registration.
/// </summary>
public sealed class HotkeyController
{
    private readonly HotkeyStore _store;
    private readonly IHotkeyRegistrar _registrar;
    private HotkeyConfig _current = HotkeyConfig.CreateDefault();
    private readonly Dictionary<HotkeyActionId, HotkeyStatus> _status = new();
    private readonly HashSet<HotkeyActionId> _registered = new();

    public string? LoadError { get; private set; }

    public HotkeyController(HotkeyStore store, IHotkeyRegistrar registrar)
    {
        _store = store;
        _registrar = registrar;
        foreach (var a in HotkeyActions.All) _status[a] = HotkeyStatus.Off;
    }

    public HotkeyConfig Current => _current.Clone();

    public IReadOnlyDictionary<HotkeyActionId, HotkeyStatus> Statuses => new Dictionary<HotkeyActionId, HotkeyStatus>(_status);

    public bool IsRegistered(HotkeyActionId a) => _registered.Contains(a);

    public void Initialize()
    {
        _current = _store.Load(out var err);
        LoadError = err;
        foreach (var a in HotkeyActions.All) RegisterOne(a);
    }

    public HotkeyCommitResult Commit(HotkeyConfig proposed)
    {
        var p = proposed.Clone();
        var errs = p.Validate();
        if (errs.Count > 0) return new HotkeyCommitResult(HotkeyCommitOutcome.Invalid, errs, Statuses);

        bool contentSame = p.ContentEquals(_current);
        if (!contentSame)
        {
            try
            {
                _store.Save(p);
            }
            catch (Exception ex)
            {
                var reason = ex is IOException or UnauthorizedAccessException
                    ? $"다른 프로그램이 파일을 사용 중이거나 쓰기 권한이 없습니다 ({ex.GetType().Name})."
                    : $"{ex.GetType().Name}";
                return new HotkeyCommitResult(HotkeyCommitOutcome.SaveFailed,
                    new[] { "단축키 설정을 저장하지 못했습니다: " + reason }, Statuses);
            }
        }

        var old = _current;
        var changed = HotkeyActions.All.Where(a =>
        {
            var o = old.Get(a);
            var n = p.Get(a);
            return o.Enabled != n.Enabled || !o.SameCombo(n) || (n.Enabled && !_registered.Contains(a));
        }).ToList();

        // Unregister every changed action first so swapped combos cannot collide with ourselves.
        foreach (var a in changed) UnregisterOne(a);
        _current = p;
        foreach (var a in changed) RegisterOne(a);

        return new HotkeyCommitResult(HotkeyCommitOutcome.Applied, Array.Empty<string>(), Statuses);
    }

    /// <summary>Release every registration (app exit).</summary>
    public void Shutdown()
    {
        foreach (var a in _registered.ToList()) UnregisterOne(a);
    }

    private void RegisterOne(HotkeyActionId a)
    {
        var b = _current.Get(a);
        if (!b.Enabled)
        {
            _status[a] = HotkeyStatus.Off;
            return;
        }
        if (HotkeyRules.ValidateCombo(b.Ctrl, b.Alt, b.Shift, b.Key) != null)
        {
            _status[a] = new HotkeyStatus(HotkeyRegState.Failed, 0);
            return;
        }
        if (_registrar.TryRegister(HotkeyActions.RegistrationId(a), HotkeyRules.NativeModifiers(b), HotkeyRules.VirtualKey(b), out var err))
        {
            _registered.Add(a);
            _status[a] = new HotkeyStatus(HotkeyRegState.Registered, 0);
        }
        else
        {
            _status[a] = new HotkeyStatus(HotkeyRegState.Failed, err);
        }
    }

    private void UnregisterOne(HotkeyActionId a)
    {
        if (_registered.Remove(a)) _registrar.Unregister(HotkeyActions.RegistrationId(a));
        _status[a] = HotkeyStatus.Off;
    }
}
