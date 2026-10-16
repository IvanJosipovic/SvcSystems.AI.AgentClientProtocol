using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class SchemaGeneratorOptions
{
    private readonly HashSet<string> _allowUnknownDiscriminators;
    private readonly HashSet<string> _reuseAllOfVariants;
    private readonly Dictionary<string, Dictionary<string, string>> _defaultDiscriminatorValues;

    private SchemaGeneratorOptions(
        HashSet<string> allowUnknownDiscriminators,
        HashSet<string> reuseAllOfVariants,
        Dictionary<string, Dictionary<string, string>> defaultDiscriminatorValues)
    {
        _allowUnknownDiscriminators = allowUnknownDiscriminators;
        _reuseAllOfVariants = reuseAllOfVariants;
        _defaultDiscriminatorValues = defaultDiscriminatorValues;
    }

    public static SchemaGeneratorOptions Empty { get; } = new([], [], new(StringComparer.Ordinal));

    public static SchemaGeneratorOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;
        var allowUnknown = ReadNames(root, "allowUnknownDiscriminators");
        var reuseAllOf = ReadNames(root, "reuseAllOfVariants");
        var defaults = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("defaultDiscriminatorValues", out var defaultsElement) &&
            defaultsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var union in defaultsElement.EnumerateObject())
            {
                if (union.Value.ValueKind != JsonValueKind.Object) continue;
                var values = union.Value.EnumerateObject()
                    .Where(item => item.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
                defaults[union.Name] = values;
            }
        }

        return new SchemaGeneratorOptions(allowUnknown, reuseAllOf, defaults);
    }

    public bool AllowsUnknownDiscriminators(string unionName) => _allowUnknownDiscriminators.Contains(unionName);

    public bool ReusesAllOfVariants(string unionName) => _reuseAllOfVariants.Contains(unionName);

    public string? GetDefaultDiscriminatorValue(string unionName, string? branchTitle) =>
        branchTitle is not null && _defaultDiscriminatorValues.TryGetValue(unionName, out var values) &&
        values.TryGetValue(branchTitle, out var value)
            ? value
            : null;

    private static HashSet<string> ReadNames(JsonElement root, string propertyName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array) return names;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name) names.Add(name);
        }
        return names;
    }
}
