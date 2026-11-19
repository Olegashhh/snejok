using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinterDesktop.Core.Models;

namespace WinterDesktop.Core.Settings;

/// <summary>
/// Сериализация и валидация настроек. Отдельный класс нужен, чтобы импорт/экспорт и
/// загрузка из файла использовали ровно один набор правил (§5).
/// </summary>
public static class SettingsSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    /// <summary>
    /// Разбирает JSON, приводит схему к актуальной, восстанавливает отсутствующие секции и
    /// заново применяет ограничения диапазонов (десериализация обходит проверки в сеттерах).
    /// </summary>
    /// <exception cref="JsonException">JSON структурно неверен.</exception>
    public static AppSettings Deserialize(string json) => Deserialize(json, out _);

    /// <summary>
    /// Разбирает JSON и сообщает через <paramref name="schemaUpgraded"/>, что файл был
    /// записан более старой схемой и его стоит перезаписать в актуальном виде.
    /// </summary>
    public static AppSettings Deserialize(string json, out bool schemaUpgraded)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("Корневой элемент настроек должен быть объектом.");

        int version = ReadSchemaVersion(root);
        schemaUpgraded = version < AppSettings.CurrentSchemaVersion;
        if (version > AppSettings.CurrentSchemaVersion)
        {
            // Не разрушаем файл будущей версии: читаем что можем, остальное — по умолчанию.
            root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        }

        var settings = root.Deserialize<AppSettings>(Options) ?? new AppSettings();
        settings = Normalize(settings);

        // Схема 2: сугробы по умолчанию стали выше (18 → 24 DIP). Поднимаем только
        // значение, которое осталось равным старому дефолту; ручной выбор не трогаем.
        if (version < 2 && Math.Abs(settings.Accumulation.MaxThicknessDip - 18.0) < 0.01)
        {
            settings.Accumulation.MaxThicknessDip = 24.0;
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    /// <summary>Восстанавливает инварианты: не-null секции, диапазоны значений, словарь мониторов.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.General = NonNull(settings.General);
        settings.Snow = NonNull(settings.Snow);
        settings.Accumulation = NonNull(settings.Accumulation);
        settings.Tools = NonNull(settings.Tools);
        settings.Garlands = NonNull(settings.Garlands);
        settings.Performance = NonNull(settings.Performance);
        settings.Monitors = NonNull(settings.Monitors);
        settings.Monitors.Overrides = EnsureDictionary(settings.Monitors.Overrides);
        settings.Snowmen = NonNull(settings.Snowmen);
        settings.Snowmen.Items = SanitizeSnowmen(settings.Snowmen.Items);
        settings.General.Language = string.IsNullOrWhiteSpace(settings.General.Language) ? "ru" : settings.General.Language;

        // Повторная запись значений включает проверки диапазонов в сеттерах.
        foreach (var node in SettingsTree.Enumerate(settings))
        {
            foreach (var property in node.GetType().GetProperties())
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                if (IsSimple(property.PropertyType)) property.SetValue(node, property.GetValue(node));
            }
        }

        foreach (var pair in settings.Monitors.Overrides.ToList())
        {
            settings.Monitors.Overrides[pair.Key] = NonNull(pair.Value);
        }

        return settings;
    }

    private static int ReadSchemaVersion(JsonObject root)
    {
        if (root.TryGetPropertyValue("schemaVersion", out var node) && node is not null)
        {
            try
            {
                return node.GetValue<int>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return 0;
            }
        }
        return 0;
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);

    private static T NonNull<T>(T? value) where T : class, new() => value ?? new T();

    private const int MaxSnowmen = 64;

    private static Dictionary<string, MonitorSettings> EnsureDictionary(Dictionary<string, MonitorSettings>? source)
    {
        var result = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    /// <summary>
    /// Проверяет сохранённых снеговиков: конечные координаты, существующий монитор,
    /// число шаров 1–3, масштаб 0.5–2.5 и жёсткий предел количества.
    /// </summary>
    private static List<SnowmanRecord> SanitizeSnowmen(List<SnowmanRecord>? source)
    {
        var result = new List<SnowmanRecord>();
        if (source is null) return result;

        foreach (var item in source)
        {
            if (item is null) continue;
            if (string.IsNullOrWhiteSpace(item.Monitor)) continue;
            if (!double.IsFinite(item.X) || !double.IsFinite(item.BaseY) || !double.IsFinite(item.Scale)) continue;

            result.Add(new SnowmanRecord
            {
                Monitor = item.Monitor.Trim(),
                X = item.X,
                BaseY = item.BaseY,
                Scale = Math.Clamp(item.Scale, 0.5, 2.5),
                Balls = Math.Clamp(item.Balls, 1, 3),
            });

            if (result.Count >= MaxSnowmen) break;
        }

        return result;
    }
}
