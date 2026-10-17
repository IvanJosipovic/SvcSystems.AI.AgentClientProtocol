using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class SchemaGeneratorOptions
{
    private const string DefaultModelNamespace = "SvcSystems.AI.AgentClientProtocol.Generated";
    private const string DefaultSerializationNamespace = "SvcSystems.AI.AgentClientProtocol.Serialization";
    private const string DefaultJsonContextName = "JsonContext";

    private readonly HashSet<string> _allowUnknownDiscriminators;
    private readonly Dictionary<string, string> _defaultUnionCases;

    private SchemaGeneratorOptions(
        HashSet<string> allowUnknownDiscriminators,
        Dictionary<string, string> defaultUnionCases,
        string modelNamespace,
        string? clientNamespace,
        string? agentNamespace,
        string serializationNamespace,
        string jsonContextName,
        bool generateJsonContext)
    {
        _allowUnknownDiscriminators = allowUnknownDiscriminators;
        _defaultUnionCases = defaultUnionCases;
        ModelNamespace = modelNamespace;
        ClientNamespace = clientNamespace;
        AgentNamespace = agentNamespace;
        SerializationNamespace = serializationNamespace;
        JsonContextName = jsonContextName;
        GenerateJsonContext = generateJsonContext;
    }

    public static SchemaGeneratorOptions Empty { get; } = new(
        [], new(StringComparer.Ordinal),
        DefaultModelNamespace, null, null, DefaultSerializationNamespace, DefaultJsonContextName, false);

    public string ModelNamespace { get; }
    public string? ClientNamespace { get; }
    public string? AgentNamespace { get; }
    public string SerializationNamespace { get; }
    public string JsonContextName { get; }
    public bool GenerateJsonContext { get; }

    public static SchemaGeneratorOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;
        var allowUnknown = ReadNames(root, "allowUnknownDiscriminators");
        var defaultUnionCases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("defaultUnionCases", out var defaultCasesElement) &&
            defaultCasesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var union in defaultCasesElement.EnumerateObject())
            {
                if (union.Value.ValueKind == JsonValueKind.String && union.Value.GetString() is { Length: > 0 } caseName)
                    defaultUnionCases[union.Name] = caseName;
            }
        }

        return new SchemaGeneratorOptions(
            allowUnknown,
            defaultUnionCases,
            ReadString(root, "namespace") ?? DefaultModelNamespace,
            ReadString(root, "clientNamespace"),
            ReadString(root, "agentNamespace"),
            ReadString(root, "serializationNamespace") ?? DefaultSerializationNamespace,
            ReadString(root, "jsonContextName") ?? DefaultJsonContextName,
            ReadBoolean(root, "generateJsonContext"));
    }

    public bool AllowsUnknownDiscriminators(string unionName) => _allowUnknownDiscriminators.Contains(unionName);

    public string? GetDefaultUnionCase(string unionName) =>
        _defaultUnionCases.TryGetValue(unionName, out var caseName) ? caseName : null;

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

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static bool ReadBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;
}
