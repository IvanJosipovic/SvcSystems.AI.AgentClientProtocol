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
}

internal sealed class ClassModel(
    string name,
    IReadOnlyList<PropertyModel> properties,
    string? baseType = null,
    string? discriminatorName = null,
    IReadOnlyList<DiscriminatorCase>? discriminatorCases = null,
    bool hasExtensionData = false,
    bool allowUnknownDiscriminator = false,
    bool isRawPayloadVariant = false) : TypeModel(name)
{
    public IReadOnlyList<PropertyModel> Properties { get; } = properties;
    public string? BaseType { get; } = baseType;
    public string? DiscriminatorName { get; } = discriminatorName;
    public IReadOnlyList<DiscriminatorCase> DiscriminatorCases { get; } = discriminatorCases ?? [];
    public bool HasExtensionData { get; } = hasExtensionData;
    public bool AllowUnknownDiscriminator { get; } = allowUnknownDiscriminator;
    public bool IsRawPayloadVariant { get; } = isRawPayloadVariant;
    public string? UnknownVariantTypeName => AllowUnknownDiscriminator ? Name + "Unknown" : null;
}

internal sealed class EnumModel(string name, IReadOnlyList<string> values) : TypeModel(name)
{
    public IReadOnlyList<string> Values { get; } = values;
}

internal sealed class UnionModel(string name, IReadOnlyList<string> cases, bool structuralClassifier) : TypeModel(name)
{
    public IReadOnlyList<string> Cases { get; } = cases;
    public bool StructuralClassifier { get; } = structuralClassifier;
}

internal sealed class PropertyModel(
    string wireName,
    string identifier,
    string typeName,
    bool isRequired,
    bool isNullable,
    string? constValue = null,
    bool isIgnored = false)
{
    public string WireName { get; } = wireName;
    public string Identifier { get; } = identifier;
    public string TypeName { get; } = typeName;
    public bool IsRequired { get; } = isRequired;
    public bool IsNullable { get; } = isNullable;
    public string? ConstValue { get; } = constValue;
    public bool IsIgnored { get; } = isIgnored;
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
