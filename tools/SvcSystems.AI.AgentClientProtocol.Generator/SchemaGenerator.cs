using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

/// <summary>Generates C# models and protocol helpers from schema, metadata, and generator-options AdditionalFiles.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SchemaGenerator : IIncrementalGenerator
{
    private const string SchemaSuffix = ".schema.json";
    private const string UpstreamSchemaFileName = "schema.json";
    private const string OptionsSuffix = ".generator.json";
    private const string MetadataSuffix = ".meta.json";
    private const string UpstreamMetadataFileName = "meta.json";

    private static readonly DiagnosticDescriptor MissingSchema = new(
        "ACPGEN001", "JSON Schema AdditionalFile is missing",
        "Add a JSON Schema input ending in '{0}' or named 'schema.json'",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSchema = new(
        "ACPGEN002", "JSON Schema could not be parsed",
        "Microsoft.OpenApi could not parse schema '{0}': {1}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedSchema = new(
        "ACPGEN003", "JSON Schema construct is not supported by the generator",
        "Schema definition '{0}' contains an unsupported construct: {1}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnmatchedOptions = new(
        "ACPGEN004", "Generator options file has no matching schema",
        "Generator options file '{0}' must have a matching '{1}' file with the same name prefix",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateOptions = new(
        "ACPGEN005", "Multiple generator options files match a schema",
        "Schema '{0}' has multiple matching generator options files",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidOptions = new(
        "ACPGEN006", "Generator options could not be parsed",
        "Generator options file '{0}' is invalid: {1}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateMetadata = new(
        "ACPGEN007", "Multiple protocol metadata files match a schema",
        "Schema '{0}' has multiple matching '{1}' files",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMetadata = new(
        "ACPGEN008", "Protocol metadata could not be parsed",
        "Protocol metadata file '{0}' is invalid: {1}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>Registers schema, protocol metadata, and generator-options inputs for incremental code generation.</summary>
    /// <param name="context">The source generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var schemaInputs = context.AdditionalTextsProvider
            .Where(static file => IsSchemaPath(file.Path))
            .Select(static (file, cancellationToken) => SchemaInput.Create(file, cancellationToken))
            .Collect();

        var generatorOptions = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(OptionsSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => GeneratorOptionsInput.Create(file, cancellationToken))
            .Collect();

        var protocolMetadata = context.AdditionalTextsProvider
            .Where(static file => IsMetadataPath(file.Path))
            .Select(static (file, cancellationToken) => ProtocolMetadataInput.Create(file, cancellationToken))
            .Collect();

        // Combining all protocol inputs makes edits, additions, and removals invalidate generation.
        var inputs = schemaInputs.Combine(generatorOptions).Combine(protocolMetadata)
            .Select(static (combined, _) => (
                Schemas: combined.Left.Left,
                Options: combined.Left.Right,
                Metadata: combined.Right));
        context.RegisterPreCompilationSourceOutput(inputs, GenerateModels);
        context.RegisterSourceOutput(inputs, GenerateInfrastructure);
    }

    private static void GenerateModels(
        PreCompilationSourceProductionContext context,
        (ImmutableArray<SchemaInput> Schemas, ImmutableArray<GeneratorOptionsInput> Options, ImmutableArray<ProtocolMetadataInput> Metadata) inputs)
    {
        var generatedSources = new List<(MatchedSchema Input, GeneratedSource Source)>();
        foreach (var input in MatchSchemas(null, inputs.Schemas, inputs.Options, inputs.Metadata))
        {
            var parseResult = SchemaParser.Parse(PrepareSchemaText(input.Schema, inputs.Schemas));
            if (parseResult.Schema is null) continue;

            var model = new SchemaModelBuilder(null, UnsupportedSchema, input.Options).Build(parseResult.Schema);
            generatedSources.AddRange(ModelRenderer.RenderModels(model, input.Options).Select(source => (input, source)));
        }

        var duplicateHints = new HashSet<string>(
            generatedSources.GroupBy(item => item.Source.HintName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
            StringComparer.Ordinal);
        foreach (var (input, source) in generatedSources)
        {
            context.AddSource(CreateHintName(input.Schema.PairKey, source.HintName, duplicateHints.Contains(source.HintName)),
                CreateSourceText(source.Text));
        }
    }

    private static void GenerateInfrastructure(
        SourceProductionContext context,
        (ImmutableArray<SchemaInput> Schemas, ImmutableArray<GeneratorOptionsInput> Options, ImmutableArray<ProtocolMetadataInput> Metadata) inputs)
    {
        if (inputs.Schemas.IsDefaultOrEmpty)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSchema, Location.None, SchemaSuffix));
            return;
        }

        var matchedPairs = MatchSchemas(context.ReportDiagnostic, inputs.Schemas, inputs.Options, inputs.Metadata).ToArray();

        // An options file is unmatched only when no schema shares its name prefix. When several options
        // files match one schema the pair is reported as a duplicate (ACPGEN005) and skipped, so those
        // options must not additionally be reported as unmatched.
        var schemaKeys = new HashSet<string>(
            inputs.Schemas
                .Where(schema => !IsRegistryAgentSchema(schema, inputs.Schemas))
                .Select(schema => schema.PairKey),
            StringComparer.OrdinalIgnoreCase);

        foreach (var options in inputs.Options)
        {
            if (!schemaKeys.Contains(options.PairKey))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnmatchedOptions, Location.None, options.Path, SchemaSuffix));
            }
        }

        var generatedSources = new List<(MatchedSchema Input, GeneratedSource Source)>();
        foreach (var input in matchedPairs)
        {
            var parseResult = SchemaParser.Parse(PrepareSchemaText(input.Schema, inputs.Schemas));
            if (parseResult.Schema is null)
            {
                foreach (var error in parseResult.Errors)
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidSchema, Location.None, input.Schema.Path, error));
                }
                continue;
            }

            var model = new SchemaModelBuilder(context.ReportDiagnostic, UnsupportedSchema, input.Options).Build(parseResult.Schema);
            generatedSources.AddRange(ModelRenderer.RenderInfrastructure(model, input.Options, input.ProtocolMethods)
                .Select(source => (input, source)));
        }

        var duplicateHints = new HashSet<string>(
            generatedSources.GroupBy(item => item.Source.HintName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
            StringComparer.Ordinal);
        foreach (var (input, source) in generatedSources)
        {
            context.AddSource(CreateHintName(input.Schema.PairKey, source.HintName, duplicateHints.Contains(source.HintName)),
                CreateSourceText(source.Text));
        }
    }

    private static IEnumerable<MatchedSchema> MatchSchemas(
        Action<Diagnostic>? reportDiagnostic,
        ImmutableArray<SchemaInput> schemas,
        ImmutableArray<GeneratorOptionsInput> options,
        ImmutableArray<ProtocolMetadataInput> metadata)
    {
        // Sort by path so the emission/hint order does not depend on the AdditionalFiles enumeration order.
        foreach (var schema in schemas.Where(schema => !IsRegistryAgentSchema(schema, schemas))
                     .OrderBy(schema => schema.Path, StringComparer.OrdinalIgnoreCase))
        {
            var matches = options.Where(option => string.Equals(option.PairKey, schema.PairKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                reportDiagnostic?.Invoke(Diagnostic.Create(DuplicateOptions, Location.None, schema.Path));
                continue;
            }

            var optionsInput = matches.FirstOrDefault();
            SchemaGeneratorOptions parsedOptions;
            try
            {
                parsedOptions = SchemaGeneratorOptions.Parse(optionsInput?.Text);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
            {
                reportDiagnostic?.Invoke(Diagnostic.Create(InvalidOptions, Location.None, optionsInput?.Path, exception.Message));
                parsedOptions = SchemaGeneratorOptions.Empty;
            }

            var metadataMatches = metadata.Where(item => string.Equals(item.PairKey, schema.PairKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (metadataMatches.Length > 1)
            {
                reportDiagnostic?.Invoke(Diagnostic.Create(DuplicateMetadata, Location.None, schema.Path, MetadataSuffix));
                continue;
            }

            IReadOnlyList<(string Name, string Value)>? protocolMethods = null;
            var metadataInput = metadataMatches.FirstOrDefault();
            if (metadataInput is not null)
            {
                try
                {
                    protocolMethods = ParseProtocolMethods(metadataInput.Text);
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
                {
                    reportDiagnostic?.Invoke(Diagnostic.Create(InvalidMetadata, Location.None, metadataInput.Path, exception.Message));
                }
            }

            yield return new MatchedSchema(schema, optionsInput, parsedOptions, protocolMethods);
        }
    }

    private static IReadOnlyList<(string Name, string Value)> ParseProtocolMethods(string text)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidOperationException("The metadata root must be an object.");

        var methods = new List<(string Name, string Value)>();
        foreach (var sectionName in new[] { "agentMethods", "clientMethods", "protocolMethods" })
        {
            if (!document.RootElement.TryGetProperty(sectionName, out var section)) continue;
            if (section.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidOperationException($"'{sectionName}' must be an object.");

            foreach (var method in section.EnumerateObject())
            {
                if (method.Value.ValueKind != System.Text.Json.JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(method.Value.GetString()))
                {
                    throw new InvalidOperationException($"'{sectionName}.{method.Name}' must have a non-empty string value.");
                }
                methods.Add((method.Name, method.Value.GetString()!));
            }
        }

        return methods;
    }

    private static SourceText CreateSourceText(string source) =>
        SourceText.From(source.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"), System.Text.Encoding.UTF8);
    private static string CreateHintName(string pairKey, string sourceHintName, bool includePairPrefix)
    {
        var normalizedPath = pairKey.Replace('\\', '/');
        var schemaRoot = normalizedPath.LastIndexOf("/schema/", StringComparison.OrdinalIgnoreCase);
        var stablePath = schemaRoot >= 0
            ? normalizedPath.Substring(schemaRoot + "/schema/".Length)
            : normalizedPath.Substring(normalizedPath.LastIndexOf('/') + 1);
        var lastSeparator = stablePath.LastIndexOf('/');
        var schemaName = lastSeparator >= 0 ? stablePath.Substring(lastSeparator + 1) : stablePath;
        var prefix = schemaName + ".";
        if (includePairPrefix && lastSeparator >= 0)
        {
            var directory = stablePath.Substring(0, lastSeparator);
            var directoryPrefix = new string(directory.Select(character => char.IsLetterOrDigit(character) ? character : '.').ToArray());
            prefix += directoryPrefix + ".";
        }

        return prefix + sourceHintName;
    }

    private static string GetPairKey(string path, string suffix)
    {
        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.Substring(0, normalizedPath.Length - suffix.Length);
    }

    private static bool IsRegistryAgentSchema(SchemaInput schema, ImmutableArray<SchemaInput> schemas)
    {
        if (!Path.GetFileName(schema.Path).Equals("agent.schema.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var schemaDirectory = Path.GetDirectoryName(schema.Path);
        return schemas.Any(candidate =>
            Path.GetFileName(candidate.Path).Equals("registry.schema.json", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(candidate.Path), schemaDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrepareSchemaText(SchemaInput schema, ImmutableArray<SchemaInput> schemas)
    {
        if (!Path.GetFileName(schema.Path).Equals("registry.schema.json", StringComparison.OrdinalIgnoreCase))
        {
            return schema.Text;
        }

        var schemaDirectory = Path.GetDirectoryName(schema.Path);
        var agentSchema = schemas.FirstOrDefault(candidate =>
            Path.GetFileName(candidate.Path).Equals("agent.schema.json", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(candidate.Path), schemaDirectory, StringComparison.OrdinalIgnoreCase));
        if (agentSchema is null)
        {
            return schema.Text;
        }

        var registryRoot = JsonNode.Parse(schema.Text)?.AsObject()
            ?? throw new InvalidOperationException($"Registry schema '{schema.Path}' must have an object root.");
        var agentRoot = JsonNode.Parse(agentSchema.Text)?.AsObject()
            ?? throw new InvalidOperationException($"Agent schema '{agentSchema.Path}' must have an object root.");

        // The agent schema is draft-07 and points at "#/definitions/...", but the merged document
        // uses "$defs". Rewrite the references in the JSON tree instead of re-serializing the text.
        RewriteDefinitionsRefs(agentRoot);
        var defs = agentRoot["definitions"]?.DeepClone() as JsonObject ?? new JsonObject();

        var agentDefinition = (JsonObject)agentRoot.DeepClone();
        agentDefinition.Remove("$schema");
        agentDefinition.Remove("$id");
        agentDefinition.Remove("definitions");
        defs["RegistryAgent"] = agentDefinition;

        var registryDefinition = (JsonObject)registryRoot.DeepClone();
        registryDefinition.Remove("$schema");
        registryDefinition.Remove("$id");
        registryDefinition.Remove("definitions");

        if (registryDefinition["properties"]?["agents"]?["items"] is JsonObject agentItems &&
            agentItems["$ref"]?.GetValue<string>() == "agent.schema.json")
        {
            agentItems["$ref"] = "#/$defs/RegistryAgent";
        }
        defs["RegistryIndex"] = registryDefinition;

        var mergedSchema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = defs
        };
        return mergedSchema.ToJsonString();
    }

    /// <summary>Rewrites draft-07 <c>#/definitions/...</c> references to <c>#/$defs/...</c> in place.</summary>
    private static void RewriteDefinitionsRefs(JsonNode? node)
    {
        const string prefix = "#/definitions/";

        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue referenceValue &&
                    referenceValue.TryGetValue<string>(out var reference) &&
                    reference.StartsWith(prefix, StringComparison.Ordinal))
                {
                    obj["$ref"] = "#/$defs/" + reference.Substring(prefix.Length);
                }
                // Materialize the properties so assigning above cannot invalidate the enumeration.
                foreach (var property in obj.ToArray()) RewriteDefinitionsRefs(property.Value);
                break;
            case JsonArray array:
                foreach (var item in array.ToArray()) RewriteDefinitionsRefs(item);
                break;
        }
    }

    private static bool IsSchemaPath(string path) =>
        path.EndsWith(SchemaSuffix, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(UpstreamSchemaFileName, StringComparison.OrdinalIgnoreCase);

    private static string GetSchemaPairKey(string path) =>
        Path.GetFileName(path).Equals(UpstreamSchemaFileName, StringComparison.OrdinalIgnoreCase)
            ? GetPairKey(path, ".json")
            : GetPairKey(path, SchemaSuffix);

    private static bool IsMetadataPath(string path) =>
        path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(UpstreamMetadataFileName, StringComparison.OrdinalIgnoreCase);

    private static string GetMetadataPairKey(string path) =>
        Path.GetFileName(path).Equals(UpstreamMetadataFileName, StringComparison.OrdinalIgnoreCase)
            ? GetPairKey(path, UpstreamMetadataFileName) + "schema"
            : GetPairKey(path, MetadataSuffix);

    private sealed class SchemaInput(string path, string pairKey, string? text)
    {
        public string Path { get; } = path;
        public string PairKey { get; } = pairKey;
        public string Text { get; } = text ?? string.Empty;

        public static SchemaInput Create(AdditionalText file, System.Threading.CancellationToken cancellationToken) =>
            new(file.Path, GetSchemaPairKey(file.Path), file.GetText(cancellationToken)?.ToString());
    }

    private sealed class GeneratorOptionsInput(string path, string pairKey, string? text)
    {
        public string Path { get; } = path;
        public string PairKey { get; } = pairKey;
        public string? Text { get; } = text;

        public static GeneratorOptionsInput Create(AdditionalText file, System.Threading.CancellationToken cancellationToken) =>
            new(file.Path, GetPairKey(file.Path, OptionsSuffix), file.GetText(cancellationToken)?.ToString());
    }

    private sealed class ProtocolMetadataInput(string path, string pairKey, string? text)
    {
        public string Path { get; } = path;
        public string PairKey { get; } = pairKey;
        public string Text { get; } = text ?? string.Empty;

        public static ProtocolMetadataInput Create(AdditionalText file, System.Threading.CancellationToken cancellationToken) =>
            new(file.Path, GetMetadataPairKey(file.Path), file.GetText(cancellationToken)?.ToString());
    }

    private sealed class MatchedSchema(
        SchemaInput schema,
        GeneratorOptionsInput? optionsInput,
        SchemaGeneratorOptions options,
        IReadOnlyList<(string Name, string Value)>? protocolMethods)
    {
        public SchemaInput Schema { get; } = schema;
        public GeneratorOptionsInput? OptionsInput { get; } = optionsInput;
        public SchemaGeneratorOptions Options { get; } = options;
        public IReadOnlyList<(string Name, string Value)>? ProtocolMethods { get; } = protocolMethods;
    }
}
