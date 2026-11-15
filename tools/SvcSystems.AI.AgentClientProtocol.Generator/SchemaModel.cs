namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class SchemaModel
{
    public List<TypeModel> Types { get; } = [];
    public List<RpcMethodModel> Methods { get; } = [];
}

internal sealed class RpcMethodModel(string name, string side, string messageType)
{
    public string Name { get; } = name;
    public string Side { get; } = side;
    public string MessageType { get; } = messageType;
}

internal abstract class TypeModel(string name)
{
    public string Name { get; } = name;
    public bool IsInternal { get; set; }
    public string? Side { get; set; }
    public string? Description { get; set; }
}

internal sealed class ClassModel(
    string name,
    IReadOnlyList<PropertyModel> properties,
    string? baseType = null,
    string? discriminatorName = null,
    IReadOnlyList<DiscriminatorCase>? discriminatorCases = null,
    bool hasExtensionData = false) : TypeModel(name)
{
    public IReadOnlyList<PropertyModel> Properties { get; } = properties;
    public string? BaseType { get; } = baseType;
    public string? DiscriminatorName { get; } = discriminatorName;
    public IReadOnlyList<DiscriminatorCase> DiscriminatorCases { get; } = discriminatorCases ?? [];
    public bool HasExtensionData { get; private set; } = hasExtensionData;

    public void EnableExtensionData() => HasExtensionData = true;
}

internal sealed class EnumModel(string name, IReadOnlyList<string> values) : TypeModel(name)
{
    public IReadOnlyList<string> Values { get; } = values;
}

internal sealed class DictionaryModel(string name, string keyType, string valueType) : TypeModel(name)
{
    public string KeyType { get; } = keyType;
    public string ValueType { get; } = valueType;
}

internal sealed class UnionModel(
    string name,
    IReadOnlyList<string> cases,
    string? defaultCaseName = null,
    IReadOnlyList<UnionCaseModel>? caseModels = null,
    bool allowUnknownDiscriminator = false,
    string? discriminatorProperty = null,
    string? unknownDiscriminatorFallbackCaseName = null) : TypeModel(name)
{
    public IReadOnlyList<string> Cases { get; } = cases;
    public string? DefaultCaseName { get; } = defaultCaseName;
    public IReadOnlyList<UnionCaseModel> CaseModels { get; } = caseModels ?? [];
    public bool AllowUnknownDiscriminator { get; } = allowUnknownDiscriminator;
    public string? DiscriminatorProperty { get; } = discriminatorProperty;
    public string? UnknownDiscriminatorFallbackCaseName { get; } = unknownDiscriminatorFallbackCaseName;
    // Value-based tags and open fallbacks need behavior the structural classifier cannot express.
    public bool UsesStructuralClassifier => CaseModels.Count == Cases.Count &&
        Cases.Count > 1 &&
        !AllowUnknownDiscriminator &&
        DefaultCaseName is null &&
        CaseModels.All(item => item.IsPlainObject && item.ConstProperties.Count == 0) &&
        HasDistinguishableObjectCases();
    public bool HasGeneratedClassifier => CaseModels.Count == Cases.Count &&
        !UsesStructuralClassifier &&
        (CaseModels.Any(item => item.ConstProperties.Count > 0) ||
         CaseModels.Any(item => item.UniqueProperties.Count > 0) ||
         DefaultCaseName is not null ||
         Cases.Count(item => item is "int" or "uint" or "long" or "ulong" or "double" or "float") > 1);

    private bool HasDistinguishableObjectCases()
    {
        for (var firstIndex = 0; firstIndex < CaseModels.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < CaseModels.Count; secondIndex++)
            {
                if (IsShadowedBy(CaseModels[firstIndex], CaseModels[secondIndex]) ||
                    IsShadowedBy(CaseModels[secondIndex], CaseModels[firstIndex]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsShadowedBy(UnionCaseModel candidate, UnionCaseModel other) =>
        candidate.Properties.All(property => other.Properties.Contains(property, StringComparer.OrdinalIgnoreCase)) &&
        other.RequiredProperties.All(property => candidate.RequiredProperties.Contains(property, StringComparer.OrdinalIgnoreCase));
}

internal sealed class UnionCaseModel(
    string typeName,
    IReadOnlyDictionary<string, string> constProperties,
    IReadOnlyList<string> properties,
    IReadOnlyList<string> requiredProperties,
    IReadOnlyList<string> uniqueProperties,
    bool isPlainObject = false)
{
    public string TypeName { get; } = typeName;
    public IReadOnlyDictionary<string, string> ConstProperties { get; } = constProperties;
    public IReadOnlyList<string> Properties { get; } = properties;
    public IReadOnlyList<string> RequiredProperties { get; } = requiredProperties;
    public IReadOnlyList<string> UniqueProperties { get; } = uniqueProperties;
    public bool IsPlainObject { get; } = isPlainObject;
}

internal sealed class PropertyModel(
    string wireName,
    string identifier,
    string typeName,
    bool isRequired,
    bool isNullable,
    string? constValue = null,
    bool isIgnored = false,
    string? description = null,
    bool defaultOnNull = false)
{
    public string WireName { get; } = wireName;
    public string Identifier { get; } = identifier;
    public string TypeName { get; } = typeName;
    public bool IsRequired { get; } = isRequired;
    public bool IsNullable { get; } = isNullable;
    public string? ConstValue { get; } = constValue;
    public bool IsIgnored { get; } = isIgnored;
    public string? Description { get; } = description;
    public bool DefaultOnNull { get; } = defaultOnNull;
}

internal sealed class DiscriminatorCase(string typeName, string discriminatorValue)
{
    public string TypeName { get; } = typeName;
    public string DiscriminatorValue { get; } = discriminatorValue;
}

internal sealed class GeneratedSource(string hintName, string text)
{
    public string HintName { get; } = hintName;
    public string Text { get; } = text;
}
