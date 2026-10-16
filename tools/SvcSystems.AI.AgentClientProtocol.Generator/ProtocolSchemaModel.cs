namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class ProtocolSchemaModel
{
    public List<ProtocolTypeModel> Types { get; } = [];
}

internal abstract class ProtocolTypeModel(string name)
{
    public string Name { get; } = name;
}

internal sealed class ProtocolClassModel(
    string name,
    IReadOnlyList<ProtocolPropertyModel> properties,
    string? baseType = null,
    string? discriminatorName = null,
    IReadOnlyList<ProtocolDiscriminatorCase>? discriminatorCases = null,
    bool hasExtensionData = false) : ProtocolTypeModel(name)
{
    public IReadOnlyList<ProtocolPropertyModel> Properties { get; } = properties;
    public string? BaseType { get; } = baseType;
    public string? DiscriminatorName { get; } = discriminatorName;
    public IReadOnlyList<ProtocolDiscriminatorCase> DiscriminatorCases { get; } = discriminatorCases ?? [];
    public bool HasExtensionData { get; } = hasExtensionData;
}

internal sealed class ProtocolEnumModel(string name, IReadOnlyList<string> values) : ProtocolTypeModel(name)
{
    public IReadOnlyList<string> Values { get; } = values;
}

internal sealed class ProtocolUnionModel(string name, IReadOnlyList<string> cases, bool structuralClassifier) : ProtocolTypeModel(name)
{
    public IReadOnlyList<string> Cases { get; } = cases;
    public bool StructuralClassifier { get; } = structuralClassifier;
}

internal sealed class ProtocolPropertyModel(string wireName, string identifier, string typeName, bool isRequired, bool isNullable)
{
    public string WireName { get; } = wireName;
    public string Identifier { get; } = identifier;
    public string TypeName { get; } = typeName;
    public bool IsRequired { get; } = isRequired;
    public bool IsNullable { get; } = isNullable;
}

internal sealed class ProtocolDiscriminatorCase(string typeName, string discriminatorValue)
{
    public string TypeName { get; } = typeName;
    public string DiscriminatorValue { get; } = discriminatorValue;
}

internal sealed class GeneratedProtocolSource(string hintName, string text)
{
    public string HintName { get; } = hintName;
    public string Text { get; } = text;
}
