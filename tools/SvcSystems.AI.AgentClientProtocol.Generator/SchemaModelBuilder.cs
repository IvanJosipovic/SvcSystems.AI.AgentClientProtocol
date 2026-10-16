using Microsoft.CodeAnalysis;
using Microsoft.OpenApi;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class SchemaModelBuilder(
    Action<Diagnostic>? reportDiagnostic,
    DiagnosticDescriptor unsupportedSchema,
    SchemaGeneratorOptions? options = null)
{
    private readonly SchemaGeneratorOptions _options = options ?? SchemaGeneratorOptions.Empty;
    private readonly Dictionary<string, IOpenApiSchema> _definitions = new(StringComparer.Ordinal);
    private readonly SchemaModel _model = new();
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _building = new(StringComparer.Ordinal);

    public SchemaModel Build(OpenApiSchema root)
    {
        foreach (var definition in root.Definitions ?? new Dictionary<string, IOpenApiSchema>())
        {
            _definitions[definition.Key] = definition.Value;
            if (definition.Value.Extensions is { } extensions &&
                extensions.TryGetValue("x-method", out var methodExtension) &&
                methodExtension is JsonNodeExtension methodNode &&
                methodNode.Node.GetValueKind() == JsonValueKind.String &&
                methodNode.Node.GetValue<string>() is { Length: > 0 } method)
            {
                var side = extensions.TryGetValue("x-side", out var sideExtension) && sideExtension is JsonNodeExtension sideNode
                    ? sideNode.Node.GetValue<string>() ?? string.Empty
                    : string.Empty;
                var messageType = definition.Key.EndsWith("Notification", StringComparison.Ordinal)
                    ? "notification"
                    : definition.Key.EndsWith("Request", StringComparison.Ordinal) ? "request" : "response";
                if (!_model.Methods.Any(item => item.Name == method && item.Side == side && item.MessageType == messageType))
                    _model.Methods.Add(new RpcMethodModel(method, side, messageType));
            }
        }

        if (_definitions.Count == 0)
        {
            AddDefinition("RootSchema", root);
        }
        else
        {
            foreach (var definition in _definitions)
            {
                if (!IsDocumentationEnvelope(definition.Value))
                {
                    AddDefinition(Identifier(definition.Key), definition.Value);
                }
            }
        }

        return _model;
    }

    private string AddDefinition(string name, IOpenApiSchema definition)
    {
        if (_emitted.Contains(name) || !_building.Add(name)) return name;
        var schema = Resolve(definition);
        if (schema is null)
        {
            ReportUnsupported(name, "unresolved definition reference");
            _building.Remove(name);
            return name;
        }

        if (OpenEnumPrimitiveType(schema) is not null)
        {
            // Extensible string/numeric enums use their primitive wire type.
        }
        else if (TryGetClosedStringEnum(schema, out var values))
        {
            _model.Types.Add(new EnumModel(name, values));
        }
        else if (IsUnion(schema))
        {
            AddUnion(name, schema);
        }
        else if (IsObject(schema))
        {
            _model.Types.Add(BuildClass(name, schema));
        }

        _building.Remove(name);
        _emitted.Add(name);
        return name;
    }

    private void AddUnion(string name, OpenApiSchema schema)
    {
        var discriminatorName = GetDiscriminatorName(name, schema);
        if (discriminatorName is not null)
        {
            AddTaggedUnion(name, schema, discriminatorName);
            return;
        }

        var branches = schema.AnyOf ?? schema.OneOf ?? [];
        var caseNames = branches
            .Where(branch => !IsNull(branch))
            .Select((branch, index) => GetTypeName(name, "Case" + index, branch, nullable: false))
            .ToArray();

        if (caseNames.Length == 0)
        {
            ReportUnsupported(name, "empty anyOf/oneOf");
            return;
        }

        var structuralClassifier = caseNames.All(caseName => _model.Types.OfType<ClassModel>().Any(type => type.Name == caseName));
        _model.Types.Add(new UnionModel(name, caseNames, structuralClassifier));
        AddConstUnionDiscriminator(name, branches.ToArray());
    }

    private void AddConstUnionDiscriminator(string name, IReadOnlyList<IOpenApiSchema> branches)
    {
        var branchConstants = branches
            .Where(branch => !IsNull(branch))
            .Select(branch => GetProperties(Resolve(branch) ?? new OpenApiSchema(), new HashSet<string>(StringComparer.Ordinal))
                .Select(property => (property.Key, Value: Resolve(property.Value.Schema)?.Const))
                .Where(property => property.Value is not null)
                .ToArray())
            .Where(properties => properties.Length == 1)
            .ToArray();
        if (branchConstants.Length == 0) return;

        var propertyName = branchConstants[0][0].Key;
        if (!branchConstants.All(properties => string.Equals(properties[0].Key, propertyName, StringComparison.Ordinal))) return;
        var values = branchConstants.Select(properties => properties[0].Value!).Distinct(StringComparer.Ordinal).ToList();
        var enumName = name + "Discriminator";
        if (_model.Types.All(type => !string.Equals(type.Name, enumName, StringComparison.Ordinal)))
        {
            _model.Types.Add(new EnumModel(enumName, ["unknown", .. values]));
        }
    }

    private void AddTaggedUnion(string name, OpenApiSchema schema, string discriminatorName)
    {
        var cases = new List<DiscriminatorCase>();
        var variants = new List<ClassModel>();
        var commonProperties = BuildProperties(name, schema, excludedProperties: [discriminatorName]);
        var index = 0;

        foreach (var branch in schema.OneOf ?? schema.AnyOf ?? [])
        {
            var branchSchema = Resolve(branch);
            if (branchSchema is null)
            {
                index++;
                continue;
            }

            var properties = GetProperties(branchSchema, new HashSet<string>(StringComparer.Ordinal));
            var discriminatorValue = properties.TryGetValue(discriminatorName, out var discriminatorProperty)
                ? Resolve(discriminatorProperty.Schema)?.Const
                : null;
            discriminatorValue ??= _options.GetDefaultDiscriminatorValue(name, branchSchema.Title);
            if (string.IsNullOrEmpty(discriminatorValue))
            {
                index++;
                continue;
            }
            var caseDefinition = GetReferenceDefinitionName(branch) ?? branchSchema.Title ?? "Variant" + index;
            var referencedVariantName = _options.ReusesAllOfVariants(name)
                ? GetSingleAllOfReferenceName(branchSchema)
                : null;
            if (referencedVariantName is not null && _definitions.TryGetValue(referencedVariantName, out var referencedDefinition))
            {
                AddDefinition(referencedVariantName, referencedDefinition);
            }
            var variantName = referencedVariantName ?? Identifier(name + Pascal(caseDefinition) + Pascal(discriminatorValue!));
            var variantProperties = commonProperties
                .Concat(BuildProperties(variantName, branchSchema, excludedProperties: [discriminatorName]))
                .GroupBy(property => property.WireName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var existingIndex = _model.Types.FindIndex(type => type.Name == variantName);
            var existing = existingIndex >= 0 ? _model.Types[existingIndex] as ClassModel : null;
            var variant = new ClassModel(
                variantName,
                existing?.Properties ?? variantProperties,
                baseType: name,
                hasExtensionData: existing?.HasExtensionData ?? (branchSchema.AdditionalPropertiesAllowed && branchSchema.AdditionalProperties is null));
            if (existingIndex >= 0) _model.Types[existingIndex] = variant;
            else variants.Add(variant);
            cases.Add(new DiscriminatorCase(variantName, discriminatorValue!));
            index++;
        }

        var allowsUnknown = _options.AllowsUnknownDiscriminators(name);
        _model.Types.Add(new ClassModel(name, [], discriminatorName: discriminatorName, discriminatorCases: cases, allowUnknownDiscriminator: allowsUnknown));
        _model.Types.AddRange(variants);
        if (allowsUnknown)
        {
            _model.Types.Add(new ClassModel(
                name + "Unknown",
                [new PropertyModel("RawPayload", "RawPayload", "JsonElement", isRequired: true, isNullable: false, isIgnored: true)],
                baseType: name,
                isRawPayloadVariant: true));
        }
        var discriminatorEnumName = name + "Discriminator";
        if (_model.Types.All(type => !string.Equals(type.Name, discriminatorEnumName, StringComparison.Ordinal)))
        {
            _model.Types.Add(new EnumModel(
                discriminatorEnumName,
                ["unknown", .. cases.Select(item => item.DiscriminatorValue)]));
        }
    }

    private ClassModel BuildClass(string name, OpenApiSchema schema)
    {
        var properties = BuildProperties(name, schema, excludedProperties: []);
        return new ClassModel(
            name,
            properties,
            hasExtensionData: schema.Properties is { Count: > 0 } && schema.AdditionalPropertiesAllowed && schema.AdditionalProperties is null);
    }

    private IReadOnlyList<PropertyModel> BuildProperties(
        string ownerName,
        OpenApiSchema schema,
        IReadOnlyCollection<string> excludedProperties)
    {
        var properties = GetProperties(schema, new HashSet<string>(StringComparer.Ordinal));
        var results = new List<PropertyModel>();
        foreach (var property in properties)
        {
            if (excludedProperties.Contains(property.Key)) continue;

            var required = property.Value.Required;
            var resolved = Resolve(property.Value.Schema);
            var nullable = !required || resolved?.Type?.HasFlag(JsonSchemaType.Null) == true || HasNullCase(resolved);
            var typeName = GetTypeName(ownerName, property.Key, property.Value.Schema, nullable);
            var identifier = Identifier(Pascal(property.Key));
            if (string.Equals(identifier, ownerName, StringComparison.Ordinal)) identifier += "Value";
            results.Add(new PropertyModel(property.Key, identifier, typeName, required, nullable, resolved?.Const));
        }
        return results;
    }

    private string GetTypeName(string owner, string property, IOpenApiSchema schema, bool nullable)
    {
        if (schema is OpenApiSchemaReference reference)
        {
            var name = GetReferenceDefinitionName(reference);
            if (name is null) return "JsonElement" + (nullable ? "?" : string.Empty);
            if (!_definitions.TryGetValue(name, out var definition))
                return "JsonElement" + (nullable ? "?" : string.Empty);
            var resolvedDefinition = Resolve(definition);
            if (resolvedDefinition is null) return "JsonElement" + (nullable ? "?" : string.Empty);
            var referenceNullable = nullable || resolvedDefinition.Type?.HasFlag(JsonSchemaType.Null) == true || HasNullCase(resolvedDefinition);
            if (resolvedDefinition.OneOf is { Count: 1 })
            {
                return GetTypeName(owner, property, resolvedDefinition.OneOf[0], referenceNullable);
            }
            if (resolvedDefinition.AnyOf is { Count: 1 })
            {
                return GetTypeName(owner, property, resolvedDefinition.AnyOf[0], referenceNullable);
            }
            if (resolvedDefinition.AllOf is { Count: 1 })
            {
                return GetTypeName(owner, property, resolvedDefinition.AllOf[0], referenceNullable);
            }
            if (resolvedDefinition.Type?.HasFlag(JsonSchemaType.Array) == true)
            {
                return GetTypeName(owner, property, resolvedDefinition, referenceNullable);
            }
            var typeName = PrimitiveType(resolvedDefinition)
                ?? OpenEnumPrimitiveType(resolvedDefinition)
                ?? AddDefinition(Identifier(name), definition);
            return ApplyNullability(typeName, referenceNullable);
        }

        var concrete = Resolve(schema);
        if (concrete is null) return "JsonElement" + (nullable ? "?" : string.Empty);
        var hasNull = nullable || HasNullCase(concrete) || concrete.Type?.HasFlag(JsonSchemaType.Null) == true;

        var nonNullAlternatives = (concrete.AnyOf ?? concrete.OneOf)?.Where(alternative => !IsNull(alternative)).ToArray();
        if (hasNull && nonNullAlternatives is { Length: 1 })
        {
            return GetTypeName(owner, property, nonNullAlternatives[0], nullable: true);
        }

        if (concrete.AllOf is { Count: 1 })
        {
            return GetTypeName(owner, property, concrete.AllOf[0], hasNull);
        }

        var openEnum = OpenEnumPrimitiveType(concrete);
        if (openEnum is not null)
        {
            return ApplyNullability(openEnum, hasNull);
        }

        if (TryGetClosedStringEnum(concrete, out var enumValues))
        {
            var enumName = Identifier(owner + Pascal(property) + "Enum");
            AddModelOnce(enumName, () => new EnumModel(enumName, enumValues));
            return ApplyNullability(enumName, hasNull);
        }

        if (IsUnion(concrete))
        {
            var unionName = Identifier(owner + Pascal(property));
            if (!_emitted.Contains(unionName))
            {
                _building.Add(unionName);
                AddUnion(unionName, concrete);
                _building.Remove(unionName);
                _emitted.Add(unionName);
            }
            return ApplyNullability(unionName, hasNull);
        }

        if (concrete.Items is not null)
        {
            return ApplyNullability("List<" + GetTypeName(owner, property + "Item", concrete.Items, nullable: false) + ">", hasNull);
        }

        if (concrete.AdditionalProperties is not null)
        {
            return ApplyNullability("Dictionary<string, " + GetTypeName(owner, property + "Value", concrete.AdditionalProperties, nullable: false) + ">", hasNull);
        }

        if (concrete.Type?.HasFlag(JsonSchemaType.Object) == true && concrete.Properties is null && concrete.AdditionalPropertiesAllowed)
        {
            return ApplyNullability("Dictionary<string, JsonElement>", hasNull);
        }

        if (concrete.Properties is { Count: > 0 } || concrete.Type?.HasFlag(JsonSchemaType.Object) == true)
        {
            var inlineName = Identifier(owner + Pascal(property));
            if (!_emitted.Contains(inlineName))
            {
                _model.Types.Add(BuildClass(inlineName, concrete));
                _emitted.Add(inlineName);
            }
            return ApplyNullability(inlineName, hasNull);
        }

        var primitive = PrimitiveType(concrete) ?? "JsonElement";
        return ApplyNullability(primitive, hasNull);
    }

    private Dictionary<string, PropertyEntry> GetProperties(OpenApiSchema schema, HashSet<string> visitedDefinitions)
    {
        var properties = new Dictionary<string, PropertyEntry>(StringComparer.Ordinal);
        if (schema.Properties is not null)
        {
            foreach (var property in schema.Properties)
            {
                properties[property.Key] = new PropertyEntry(property.Value, schema.Required?.Contains(property.Key) == true);
            }
        }

        foreach (var parent in schema.AllOf ?? [])
        {
            var target = Resolve(parent);
            if (target is null) continue;
            if (parent is OpenApiSchemaReference reference)
            {
                var definitionName = GetReferenceDefinitionName(reference);
                if (definitionName is not null && !visitedDefinitions.Add(definitionName)) continue;
            }

            foreach (var inherited in GetProperties(target, visitedDefinitions))
            {
                properties[inherited.Key] = inherited.Value;
            }
        }
        return properties;
    }

    private OpenApiSchema? Resolve(IOpenApiSchema? schema)
    {
        if (schema is null) return null;
        if (schema is OpenApiSchema concrete) return concrete;
        if (schema is OpenApiSchemaReference reference)
        {
            if (reference.Target is OpenApiSchema target) return target;
            var name = GetReferenceDefinitionName(reference);
            if (name is not null && _definitions.TryGetValue(name, out var definition) && definition is OpenApiSchema definitionSchema)
                return definitionSchema;
        }
        return null;
    }

    private string? GetDiscriminatorName(string unionName, OpenApiSchema schema)
    {
        var explicitDiscriminator = schema.Discriminator?.PropertyName;
        if (!string.IsNullOrWhiteSpace(explicitDiscriminator)) return explicitDiscriminator;

        string? commonProperty = null;
        foreach (var branch in schema.OneOf ?? schema.AnyOf ?? [])
        {
            var properties = GetProperties(Resolve(branch) ?? new OpenApiSchema(), new HashSet<string>(StringComparer.Ordinal));
            var constants = properties
                .Where(property => Resolve(property.Value.Schema)?.Const is not null)
                .Select(property => (property.Key, Value: Resolve(property.Value.Schema)!.Const!))
                .ToArray();
            if (constants.Length == 0 && _options.GetDefaultDiscriminatorValue(unionName, Resolve(branch)?.Title) is not null) continue;
            if (constants.Length != 1) return null;
            if (commonProperty is not null && !string.Equals(commonProperty, constants[0].Key, StringComparison.Ordinal)) return null;
            commonProperty = constants[0].Key;
        }
        return commonProperty;
    }

    private string? GetSingleAllOfReferenceName(OpenApiSchema schema)
    {
        if (schema.AllOf is not { Count: 1 } || schema.AllOf[0] is not OpenApiSchemaReference reference) return null;
        var name = GetReferenceDefinitionName(reference);
        return name is not null && _definitions.ContainsKey(name) ? name : null;
    }

    private string? GetReferenceDefinitionName(IOpenApiSchema schema)
    {
        var reference = schema as OpenApiSchemaReference;
        return reference is null ? null : GetReferenceDefinitionName(reference);
    }

    private static string? GetReferenceDefinitionName(OpenApiSchemaReference reference)
    {
        var id = reference.Reference?.Id;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var referenceId = id!;
        var slash = referenceId.LastIndexOf('/');
        return Identifier(slash >= 0 ? referenceId.Substring(slash + 1) : referenceId);
    }

    private static bool IsObject(OpenApiSchema schema) =>
        schema.Properties is { Count: > 0 } || schema.Type?.HasFlag(JsonSchemaType.Object) == true || schema.AllOf is { Count: > 0 };

    private static bool IsUnion(OpenApiSchema schema) =>
        schema.OneOf is { Count: > 1 } || schema.AnyOf is { Count: > 1 };

    private bool IsDocumentationEnvelope(IOpenApiSchema definition)
    {
        var schema = Resolve(definition);
        if (schema?.Extensions is null || !schema.Extensions.TryGetValue("x-docs-ignore", out var extension)) return false;
        return extension is JsonNodeExtension nodeExtension && nodeExtension.Node.GetValueKind() == JsonValueKind.True;
    }

    private bool IsNull(IOpenApiSchema schema) => Resolve(schema)?.Type == JsonSchemaType.Null;

    private bool HasNullCase(OpenApiSchema? schema) =>
        schema?.AnyOf?.Any(IsNull) == true || schema?.OneOf?.Any(IsNull) == true;

    private bool TryGetClosedStringEnum(OpenApiSchema schema, out IReadOnlyList<string> values)
    {
        values = [];

        if (schema.Enum is { Count: > 0 } enumValues)
        {
            var stringValues = new List<string>(enumValues.Count);
            foreach (var enumValue in enumValues)
            {
                if (enumValue is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var stringValue))
                {
                    return false;
                }

                stringValues.Add(stringValue);
            }

            values = stringValues;
            return true;
        }

        var alternatives = schema.OneOf;
        if (alternatives is not { Count: > 0 }) return false;
        var result = new List<string>();
        foreach (var alternative in alternatives)
        {
            var item = Resolve(alternative);
            if (item?.Const is null || item.Type?.HasFlag(JsonSchemaType.String) != true) return false;
            result.Add(item.Const);
        }
        values = result;
        return true;
    }

    private static string? PrimitiveType(OpenApiSchema schema)
    {
        if (schema.Type is not { } type) return null;
        if (type.HasFlag(JsonSchemaType.String)) return "string";
        if (type.HasFlag(JsonSchemaType.Boolean)) return "bool";
        if (type.HasFlag(JsonSchemaType.Integer)) return schema.Format switch
        {
            "uint32" => "uint", "uint64" => "ulong", "int32" => "int", _ => "long"
        };
        if (type.HasFlag(JsonSchemaType.Number)) return schema.Format == "float" ? "float" : "double";
        if (type.HasFlag(JsonSchemaType.Array)) return "List<JsonElement>";
        return null;
    }

    private string? OpenEnumPrimitiveType(OpenApiSchema schema)
    {
        var alternatives = schema.AnyOf ?? schema.OneOf;
        if (alternatives is not { Count: > 1 }) return null;
        var primitiveTypes = alternatives
            .Select(Resolve)
            .Where(item => item is not null)
            .Select(item => PrimitiveType(item!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return primitiveTypes.Length == 1 && primitiveTypes[0] is "string" or "int" or "long" or "uint" or "ulong"
            ? primitiveTypes[0]
            : null;
    }

    private void AddModelOnce(string name, Func<TypeModel> create)
    {
        if (_emitted.Add(name)) _model.Types.Add(create());
    }

    private void ReportUnsupported(string name, string detail) =>
        reportDiagnostic?.Invoke(Diagnostic.Create(unsupportedSchema, Location.None, name, detail));

    private static string ApplyNullability(string type, bool nullable) =>
        !nullable || type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?";

    private static string Pascal(string value) => string.Concat(value.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));

    private static string Identifier(string value)
    {
        var result = new string(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (result.Length == 0 || !char.IsLetter(result[0]) && result[0] != '_') result = "_" + result;
        return result;
    }

    private sealed class PropertyEntry(IOpenApiSchema schema, bool required)
    {
        public IOpenApiSchema Schema { get; } = schema;
        public bool Required { get; } = required;
    }
}
