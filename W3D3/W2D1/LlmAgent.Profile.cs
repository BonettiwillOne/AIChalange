using System.Text;
using System.Text.Json;

namespace W2D1;

internal sealed partial class LlmAgent
{
    private readonly string _profilePath = Path.Combine(AppContext.BaseDirectory, "user-profile.json");
    private UserProfileStore _profileStore = new();
    private bool _profileStoreAvailable;
    private sealed class UserProfileStore
    {
        public string ActiveProfile { get; set; } = "default";
        public Dictionary<string, UserProfile> Profiles { get; set; } = new(StringComparer.Ordinal)
        {
            ["default"] = new UserProfile()
        };
    }

    private const string ProfileHelp = "Команды: /profiles; /profile; /profile create имя; /profile switch имя; /profile delete имя; " +
        "/profile set key=value; /profile remove key; /profile clear.\n" +
        "Поля: name, expertise, response_style, response_length, format, language, constraints.";

    private static bool ValidProfileId(string id) => !string.IsNullOrWhiteSpace(id)
        && id.Length <= 80 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);

    private static UserProfileStore ReadProfileStore(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("activeProfile", out _)
            || !root.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Object
            || profiles.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                != profiles.EnumerateObject().Count()) throw new JsonException();
        foreach (var profile in profiles.EnumerateObject()) ValidateProfile(profile.Value);
        var store = JsonSerializer.Deserialize<UserProfileStore>(json, HistoryOptions);
        if (store is null || store.Profiles is null || !ValidProfileId(store.ActiveProfile)
            || !store.Profiles.ContainsKey(store.ActiveProfile)
            || store.Profiles.Any(p => !ValidProfileId(p.Key) || p.Value is null)) throw new JsonException();
        return store;
    }

    // Не допускаем тихой потери незнакомых полей при десериализации и следующей записи.
    private static void ValidateProfile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.EnumerateObject())
            if (!seen.Add(field.Name)
                || new UserProfile().WithField(field.Name, null) is null
                || field.Name != field.Name.ToLowerInvariant()
                || field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException();
    }

    private string LoadUserProfile()
    {
        try
        {
            if (!File.Exists(_profilePath))
            {
                SaveProfileStore(_profileStore);
                return "Выбран пустой профиль: default. Настройка: /profile set key=value.";
            }
            var json = SanitizeMemory(File.ReadAllText(_profilePath, Encoding.UTF8));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (document.RootElement.TryGetProperty("profiles", out _)
                || document.RootElement.TryGetProperty("activeProfile", out _))
                _profileStore = ReadProfileStore(json);
            else
            {
                // Старый одиночный профиль становится default без изменения его полей.
                var legacy = JsonSerializer.Deserialize<UserProfile>(json, HistoryOptions) ?? throw new JsonException();
                ValidateProfile(document.RootElement);
                var migrated = new UserProfileStore();
                migrated.Profiles["default"] = legacy;
                // Сохраняем исходный одиночный профиль до атомарной замены формата.
                File.Copy(_profilePath, _profilePath + ".migration-" + Guid.NewGuid().ToString("N") + ".bak");
                SaveProfileStore(migrated);
                return "Прежний USER PROFILE перенесён в профиль default и выбран. Просмотр: /profile.";
            }
            _profileStoreAvailable = true;
            return SanitizeMemory($"Профили загружены. Активный профиль: {_profileStore.ActiveProfile}. Просмотр: /profiles.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _profileStoreAvailable = false;
            return "Не удалось загрузить или сохранить user-profile.json. Исходный файл не перезаписан. " +
                "Операции с профилями и обычные запросы заблокированы до исправления файла и перезапуска; пустой default не подставляется.";
        }
    }

    private string BuildUserProfileBlock() => SanitizeMemory("[ACTIVE USER PROFILE]\nProfile ID: " +
        _profileStore.ActiveProfile + "\n" + JsonSerializer.Serialize(
            _profileStore.Profiles[_profileStore.ActiveProfile], HistoryOptions));

    private void SaveProfileStore(UserProfileStore next)
    {
        var json = SanitizeMemory(JsonSerializer.Serialize(next, HistoryOptions));
        var sanitized = ReadProfileStore(json);
        var temporaryPath = _profilePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, _profilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _profileStore = sanitized;
        _profileStoreAvailable = true;
    }

    private string? HandleProfileCommand(string request)
    {
        var parts = request.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        var list = parts[0].Equals("/profiles", StringComparison.OrdinalIgnoreCase);
        if (!list && !parts[0].Equals("/profile", StringComparison.OrdinalIgnoreCase)) return null;
        if (!_profileStoreAvailable) return "Хранилище профилей не загружено. Исправьте user-profile.json и перезапустите программу. Файл не изменён.";
        try
        {
            if (list) return parts.Length != 1 ? ProfileHelp : SanitizeMemory("Профили:\n" + string.Join("\n",
                _profileStore.Profiles.Keys.Select(id => (id == _profileStore.ActiveProfile ? "* " : "  ") + id)));
            if (parts.Length == 1) return BuildUserProfileBlock();
            var next = new UserProfileStore
            {
                ActiveProfile = _profileStore.ActiveProfile,
                Profiles = new Dictionary<string, UserProfile>(_profileStore.Profiles, StringComparer.Ordinal)
            };
            var action = parts[1].ToLowerInvariant();
            if (action is "create" or "switch" or "delete")
            {
                if (parts.Length != 3) return ProfileHelp;
                var id = parts[2].Trim();
                if (!ValidProfileId(id)) return "ID профиля: от 1 до 80 символов без пробелов и управляющих символов. Регистр учитывается.";
                string message;
                if (action == "create")
                {
                    if (next.Profiles.ContainsKey(id)) return "Профиль с таким ID уже существует.";
                    next.Profiles.Add(id, new UserProfile());
                    next.ActiveProfile = id;
                    message = $"Создан и выбран профиль: {id}";
                }
                else
                {
                    if (!next.Profiles.ContainsKey(id)) return "Профиль не найден. Список: /profiles.";
                    if (action == "switch")
                    {
                        next.ActiveProfile = id;
                        message = $"Активный профиль: {id}";
                    }
                    else
                    {
                        if (next.Profiles.Count == 1)
                            return "Нельзя удалить последний профиль. Создайте другой через /profile create имя или очистите текущий через /profile clear.";
                        next.Profiles.Remove(id);
                        message = $"Удалён профиль: {id}";
                        if (next.ActiveProfile == id)
                        {
                            next.ActiveProfile = next.Profiles.Keys.OrderBy(key => key, StringComparer.Ordinal).First();
                            message += $"\nАктивный профиль: {next.ActiveProfile}";
                        }
                    }
                }
                SaveProfileStore(next);
                return SanitizeMemory(message);
            }
            var current = next.Profiles[next.ActiveProfile];
            UserProfile? updated;
            if (action == "clear" && parts.Length == 2) updated = new UserProfile();
            else if (action == "remove" && parts.Length == 3)
                updated = current.WithField(parts[2].Trim(), null);
            else if (action == "set" && parts.Length == 3)
            {
                var equals = parts[2].IndexOf('=');
                if (equals <= 0 || string.IsNullOrWhiteSpace(parts[2][(equals + 1)..])) return ProfileHelp;
                updated = current.WithField(parts[2][..equals].Trim(), parts[2][(equals + 1)..].Trim());
            }
            else return ProfileHelp;
            if (updated is null) return "Неизвестное поле профиля.\n" + ProfileHelp;

            // Сначала подтверждаем запись; при ошибке действующий профиль не меняется.
            next.Profiles[next.ActiveProfile] = updated;
            SaveProfileStore(next);
            return action == "clear" ? "Активный USER PROFILE очищен и сохранён." : "Активный USER PROFILE обновлён и сохранён.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "Не удалось прочитать или сохранить USER PROFILE. Изменения не применены.";
        }
    }
}
