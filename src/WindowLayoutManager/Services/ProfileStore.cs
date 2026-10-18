using System.Text.Json;
using System.Text.Json.Nodes;
using WindowLayoutManager.Model;

namespace WindowLayoutManager.Services;

public class ProfileStore
{
    private readonly string _profilesDir;
    private readonly string _corruptDir;

    public string ProfilesDir => _profilesDir;

    public ProfileStore(string dataDir)
    {
        _profilesDir = Path.Combine(dataDir, "profiles");
        _corruptDir = Path.Combine(dataDir, "corrupt");
        Directory.CreateDirectory(_profilesDir);
    }

    public static bool IsSafeId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (id.Length > 64) return false;
        foreach (var ch in id)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')) return false;
        }
        return true;
    }

    public static string GenerateId() => Guid.NewGuid().ToString("N");

    public List<Profile> LoadAll(out List<string> loadErrors)
    {
        loadErrors = new List<string>();
        var result = new List<Profile>();
        foreach (var path in Directory.EnumerateFiles(_profilesDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var p = LoadFromFile(path);
                if (p is not null) result.Add(p);
            }
            catch (Exception ex)
            {
                loadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                QuarantineCorrupt(path, ex);
            }
        }
        return result;
    }

    public Profile? Load(string id)
    {
        if (!IsSafeId(id)) throw new InvalidOperationException("프로파일 ID가 안전하지 않습니다.");
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        return LoadFromFile(path);
    }

    private const long MaxProfileFileBytes = 1 * 1024 * 1024; // 1 MiB cap

    private static Profile? LoadFromFile(string path)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxProfileFileBytes)
            throw new InvalidDataException($"프로파일 파일이 너무 큽니다: {size} bytes (한도 {MaxProfileFileBytes})");

        var json = File.ReadAllText(path);
        var fileId = Path.GetFileNameWithoutExtension(path);

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException je) { throw new InvalidDataException($"JSON 파싱 실패: {je.Message}"); }
        if (root is null || root is not JsonObject obj)
            throw new InvalidDataException("프로파일 JSON 최상위가 객체가 아닙니다.");
        if (obj["version"] is null) throw new InvalidDataException("필수 필드 누락: version");
        if (obj["id"] is null) throw new InvalidDataException("필수 필드 누락: id");
        if (obj["name"] is null) throw new InvalidDataException("필수 필드 누락: name");
        if (obj["entries"] is null) throw new InvalidDataException("필수 필드 누락: entries");
        if (obj["entries"] is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr[i];
                if (e is null) throw new InvalidDataException($"엔트리 {i}가 null입니다.");
                if (e is not JsonObject eo) throw new InvalidDataException($"엔트리 {i}가 객체가 아닙니다.");
                foreach (var required in new[] { "id", "role", "exePath", "className", "rect" })
                {
                    if (eo[required] is null)
                        throw new InvalidDataException($"엔트리 {i}: 필수 필드 누락: {required}");
                }
                if (eo["rect"] is not JsonObject rectObj)
                    throw new InvalidDataException($"엔트리 {i}: rect가 객체가 아닙니다.");
                foreach (var rk in new[] { "x", "y", "w", "h" })
                {
                    if (rectObj[rk] is null)
                        throw new InvalidDataException($"엔트리 {i}.rect: 필수 필드 누락: {rk}");
                }
            }
        }

        var p = JsonSerializer.Deserialize<Profile>(json, JsonUtil.Options);
        if (p is null) throw new InvalidDataException("빈 프로파일입니다.");
        ValidateLoaded(p);

        if (!string.Equals(p.Id, fileId, StringComparison.Ordinal))
            throw new InvalidDataException($"파일명 ID '{fileId}'와 내용 ID '{p.Id}'가 일치하지 않습니다.");
        return p;
    }

    private static void ValidateLoaded(Profile p)
    {
        if (p.Version != Profile.CurrentVersion)
        {
            throw new InvalidDataException($"알 수 없는 프로파일 버전: {p.Version} (지원: {Profile.CurrentVersion})");
        }
        if (!IsSafeId(p.Id)) throw new InvalidDataException("프로파일 ID가 안전하지 않습니다.");
        if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidDataException("프로파일 이름이 비어있습니다.");
        if (p.Name.Length > 120) throw new InvalidDataException("프로파일 이름이 너무 깁니다.");
        if (p.Entries is null) throw new InvalidDataException("entries가 없습니다.");
        if (p.Entries.Count > 256) throw new InvalidDataException("엔트리 수가 너무 많습니다.");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in p.Entries)
        {
            if (e is null) throw new InvalidDataException("엔트리가 null입니다.");
            if (string.IsNullOrWhiteSpace(e.Id) || !seenIds.Add(e.Id))
                throw new InvalidDataException("엔트리 ID가 중복이거나 비어있습니다.");
            if (string.IsNullOrWhiteSpace(e.Role)) throw new InvalidDataException("엔트리 role이 비어있습니다.");
            if (string.IsNullOrWhiteSpace(e.ExePath)) throw new InvalidDataException("엔트리 exePath가 비어있습니다.");
            if (string.IsNullOrWhiteSpace(e.ClassName)) throw new InvalidDataException("엔트리 className이 비어있습니다.");
            if (e.Rect is null) throw new InvalidDataException("엔트리 rect가 null입니다.");
            if (e.Rect.Width < 50 || e.Rect.Width > 32000 || e.Rect.Height < 50 || e.Rect.Height > 32000)
                throw new InvalidDataException("엔트리 사각형 크기가 유효 범위를 벗어납니다.");
            if (e.Rect.X < -32000 || e.Rect.X > 32000 || e.Rect.Y < -32000 || e.Rect.Y > 32000)
                throw new InvalidDataException("엔트리 사각형 좌표가 유효 범위를 벗어납니다.");
            if (e.TitleContains is not null && e.TitleContains.Length > 256)
                throw new InvalidDataException("엔트리 titleContains가 너무 깁니다.");
        }
    }

    public virtual void Save(Profile p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        p.Version = Profile.CurrentVersion;
        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = GenerateId();
        if (!IsSafeId(p.Id)) throw new InvalidOperationException("프로파일 ID가 안전하지 않습니다.");
        if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("프로파일 이름이 비어있습니다.");
        ValidateLoaded(p);
        p.UpdatedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(p, JsonUtil.Options);
        var target = PathFor(p.Id);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(target))
        {
            File.Replace(tmp, target, null);
        }
        else
        {
            File.Move(tmp, target);
        }
    }

    public bool Delete(string id)
    {
        if (!IsSafeId(id)) return false;
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string id)
    {
        if (!IsSafeId(id)) throw new InvalidOperationException("Unsafe id");
        return Path.Combine(_profilesDir, id + ".json");
    }

    private void QuarantineCorrupt(string sourcePath, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(_corruptDir);
            var dest = Path.Combine(_corruptDir, $"{Path.GetFileName(sourcePath)}.{DateTime.UtcNow:yyyyMMddHHmmss}.bad");
            File.Copy(sourcePath, dest, overwrite: true);
            File.WriteAllText(dest + ".err.txt", ex.ToString());
            // IMPORTANT: Preserve the original corrupt file for user inspection. Do not delete.
        }
        catch
        {
            // best-effort; never bubble up
        }
    }
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly string _dataDir;

    public string LastLoadError { get; private set; } = "";

    public SettingsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _dataDir = dataDir;
        _path = Path.Combine(dataDir, "settings.json");
    }

    /// <summary>Pure validation of an existing settings.json. Returns null when valid, otherwise an
    /// explanation. Does not read LastLoadError or mutate state. Shared by Load (for user feedback)
    /// and Save (for save-time backup decisions regardless of whether Load was ever called).</summary>
    private static string? ValidateExisting(string path)
    {
        if (!File.Exists(path)) return null;
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return $"읽기 오류: {ex.Message}"; }
        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (JsonException je) { return $"JSON 파싱 실패: {je.Message}"; }
        if (root is null || root is not JsonObject obj) return "최상위가 객체가 아닙니다.";
        if (!obj.ContainsKey("version")) return "필수 필드 누락: version";
        if (obj["version"] is not JsonValue vNode || !vNode.TryGetValue<int>(out var ver)) return "version 정수가 아닙니다";
        if (ver != AppSettings.CurrentVersion) return $"알 수 없는 settings 버전: {ver}";
        // Required: activeProfileId key must be present (value may be null).
        if (!obj.ContainsKey("activeProfileId")) return "필수 필드 누락: activeProfileId";
        var ap = obj["activeProfileId"];
        if (ap is not null)
        {
            if (ap is not JsonValue av || !av.TryGetValue<string>(out var apid)) return "activeProfileId 형식 오류";
            if (apid is not null && !ProfileStore.IsSafeId(apid)) return "activeProfileId 안전하지 않음";
        }
        // Required: monitoringEnabled key must be present with bool value.
        if (!obj.ContainsKey("monitoringEnabled")) return "필수 필드 누락: monitoringEnabled";
        var me = obj["monitoringEnabled"];
        if (me is null || me is not JsonValue mv || !mv.TryGetValue<bool>(out _)) return "monitoringEnabled 형식 오류";
        return null;
    }

    public AppSettings Load()
    {
        LastLoadError = "";
        if (!File.Exists(_path)) return new AppSettings();
        var err = ValidateExisting(_path);
        if (err is not null)
        {
            FailLoad($"settings.json: {err}");
            return new AppSettings();
        }
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonUtil.Options);
            return s ?? new AppSettings();
        }
        catch (Exception ex)
        {
            FailLoad($"settings.json 역직렬화 오류: {ex.Message}");
            return new AppSettings();
        }
    }

    private void FailLoad(string message)
    {
        LastLoadError = message;
        // Preserve the original bytes next to the data dir on load error. If the backup copy
        // itself cannot be written, we still do not touch the primary file.
        try
        {
            var raw = File.ReadAllText(_path);
            var corrupt = Path.Combine(_dataDir, $"settings.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.bad");
            File.WriteAllText(corrupt, raw);
        }
        catch { }
    }

    /// <summary>
    /// Writes the new settings. Save-time backup discipline, independent of whether Load() was ever
    /// called: if the existing settings.json is unparseable or fails required-field validation, the
    /// original bytes are copied to settings.preoverwrite.&lt;ts&gt;.bad BEFORE the overwrite. If that
    /// backup copy itself fails, Save throws WITHOUT touching the original — a caller must handle
    /// the IO error and present it to the user; a false "persisted" claim is not acceptable.
    /// </summary>
    public void Save(AppSettings s)
    {
        s.Version = AppSettings.CurrentVersion;
        if (File.Exists(_path))
        {
            var err = ValidateExisting(_path);
            if (err is not null)
            {
                var backup = Path.Combine(_dataDir, $"settings.preoverwrite.{DateTime.UtcNow:yyyyMMddHHmmss}.bad");
                // Throws if backup can't be made; the original file is left untouched in that case.
                File.Copy(_path, backup, overwrite: false);
            }
        }
        var json = JsonSerializer.Serialize(s, JsonUtil.Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }
}

public static class DataPaths
{
    public static string DefaultDataDir()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appdata, "WindowLayoutManager");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
