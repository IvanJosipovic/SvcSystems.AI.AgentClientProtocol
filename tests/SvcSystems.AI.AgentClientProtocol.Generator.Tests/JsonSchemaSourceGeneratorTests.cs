using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SvcSystems.AI.AgentClientProtocol.Generator;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Generator.Tests;

public sealed class JsonSchemaSourceGeneratorTests
{
    [Fact]
    public void GeneratesModelsFromAnyJsonSchemaAdditionalFile()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "PromptRequest": {
                  "type": "object",
                  "properties": {
                    "sessionId": { "type": "string" },
                    "message": { "type": "string" },
                    "attempt": { "type": "integer", "format": "int32" }
                  },
                  "required": ["sessionId", "message"]
                },
                "Update": {
                  "oneOf": [
                    {
                      "type": "object",
                      "properties": {
                        "kind": { "type": "string", "const": "text" },
                        "text": { "type": "string" }
                      },
                      "required": ["kind", "text"]
                    },
                    {
                      "type": "object",
                      "properties": {
                        "kind": { "type": "string", "const": "finished" },
                        "reason": { "type": "string" }
                      },
                      "required": ["kind", "reason"]
                    }
                  ],
                  "discriminator": { "propertyName": "kind" }
                },
                "Payload": {
                  "anyOf": [
                    { "type": "string" },
                    { "type": "integer", "format": "int32" }
                  ]
                }
              }
            }
            """;

        var result = RunGenerator(schema, "fixtures/any-protocol-version.schema.json");

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, source => source.Contains("class PromptRequest", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("string SessionId", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("string Message", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("JsonPolymorphic(TypeDiscriminatorPropertyName = \"kind\")", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("JsonDerivedType(typeof(UpdateVariant0Text)", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("union Payload(string, int)", StringComparison.Ordinal));
    }

    [Fact]
    public void EmitsSerializerContextInPreCompilationWhenOptedIn()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SchemaGenerator().AsSourceGenerator()],
            additionalTexts: [new TestAdditionalText("protocol.schema.json", MinimalSchema)],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: ["ACP_STJ_SOURCE_GEN"]));

        driver = driver.RunGenerators(CreateCompilation(), TestContext.Current.CancellationToken);
        var sources = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToArray();

        Assert.Contains(sources, source => source.Contains("internal partial class JsonContext : JsonSerializerContext", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Protocol.Generated.Thing))", StringComparison.Ordinal));
    }

    [Fact]
    public void RoutesSafeModelGraphsThroughGeneratedSerializerContext()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "Thing": { "type": "object", "properties": { "name": { "type": "string" } } },
                "Choice": { "anyOf": [{ "type": "string" }, { "type": "integer", "format": "int32" }] },
                "Container": { "type": "object", "properties": { "choice": { "$ref": "#/$defs/Choice" } } }
              }
            }
            """;

        var driver = CreateDriver([new TestAdditionalText("stj-routing.schema.json", schema)], useSystemTextJsonContext: true);
        var result = driver.RunGenerators(CreateCompilation(), TestContext.Current.CancellationToken);
        var generatedSources = result.GetRunResult().Results.SelectMany(generator => generator.GeneratedSources).Select(source => source.SourceText.ToString()).ToArray();
        var jsonRenderer = Assert.Single(generatedSources, source => source.Contains("internal static class GeneratedJson", StringComparison.Ordinal));

        Assert.Contains("Serialize(Thing value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonContext.Default.Thing)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("Serialize(Container value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonContext.Default.Container)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("Serialize(Choice value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonContext.Default.Choice)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("DeserializeThing(JsonElement value)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("value.Deserialize(JsonContext.Default.Thing)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Deserialize(value, JsonContext.Default.Choice)", jsonRenderer, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMultipleSchemaAdditionalFilesWithDiagnostic()
    {
        var compilation = CreateCompilation();
        var driver = CreateDriver([
            new TestAdditionalText("one.schema.json", MinimalSchema),
            new TestAdditionalText("two.schema.json", MinimalSchema)]);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ACPGEN001");
    }

    [Fact]
    public void ReportsInvalidSchemaAsGeneratorDiagnostic()
    {
        var driver = CreateDriver([new TestAdditionalText("broken.json", "{ this is not json")]);
        driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(), out _, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ACPGEN002");
    }

    [Fact]
    public void NullableArraysGenerateNullableCollectionsNotUnionTypes()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "Tags": { "type": ["array", "null"], "items": { "type": "string" } },
                "Request": {
                  "type": "object",
                  "properties": {
                    "inline": { "type": ["array", "null"], "items": { "type": "string" } },
                    "referenced": { "$ref": "#/$defs/Tags" }
                  }
                }
              }
            }
            """;

        var result = RunGenerator(schema, "nullable-arrays.schema.json");

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("List<string>? Inline", StringComparison.Ordinal) &&
            source.Contains("List<string>? Referenced", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GeneratedSources, source => source.Contains("union RequestInline", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedStringEnumArraysGenerateClrEnumsAndWireValueMappings()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "TaskState": {
                  "type": "string",
                  "enum": ["in-progress", "awaiting_input", "done"]
                },
                "Task": {
                  "type": "object",
                  "properties": { "state": { "$ref": "#/$defs/TaskState" } },
                  "required": ["state"]
                }
              }
            }
            """;

        var result = RunGenerator(schema, "enum.schema.json");

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var enumSource = Assert.Single(result.GeneratedSources.Where(source => source.Contains("enum TaskState", StringComparison.Ordinal)));
        Assert.Contains("InProgress", enumSource, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"in-progress\")", enumSource, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"awaiting_input\")", enumSource, StringComparison.Ordinal);
        var jsonRenderer = Assert.Single(result.GeneratedSources.Where(source => source.Contains("internal static class GeneratedJson", StringComparison.Ordinal)));
        Assert.Contains("WriteTaskState", jsonRenderer, StringComparison.Ordinal);
    }

    [Fact]
    public void NullPairingMakesEverySchemaTypeNullable()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "NullableString": { "type": ["string", "null"] },
                "NullableObject": {
                  "type": ["object", "null"],
                  "properties": { "label": { "type": "string" } }
                },
                "Request": {
                  "type": "object",
                  "properties": {
                    "stringValue": { "type": ["string", "null"] },
                    "integerValue": { "type": ["integer", "null"], "format": "int32" },
                    "booleanValue": { "type": ["boolean", "null"] },
                    "items": { "type": ["array", "null"], "items": { "type": "string" } },
                    "objectValue": { "$ref": "#/$defs/NullableObject" },
                    "aliasValue": { "$ref": "#/$defs/NullableString" }
                  },
                  "required": ["stringValue", "integerValue", "booleanValue", "items", "objectValue", "aliasValue"]
                }
              }
            }
            """;

        var result = RunGenerator(schema, "null-pairs-any-type.json");
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var request = Assert.Single(result.GeneratedSources.Where(source => source.Contains("class Request", StringComparison.Ordinal)));

        Assert.Contains("string? StringValue", request, StringComparison.Ordinal);
        Assert.Contains("int? IntegerValue", request, StringComparison.Ordinal);
        Assert.Contains("bool? BooleanValue", request, StringComparison.Ordinal);
        Assert.Contains("List<string>? Items", request, StringComparison.Ordinal);
        Assert.Contains("NullableObject? ObjectValue", request, StringComparison.Ordinal);
        Assert.Contains("string? AliasValue", request, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedSchemaGeneratesTheCompleteWireModelSet()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "acp-v1.schema.json");
        var optionsPath = Path.Combine(AppContext.BaseDirectory, "acp-v1.generator.json");
        var schema = File.ReadAllText(schemaPath);
        var generatorOptions = File.ReadAllText(optionsPath);
        var result = RunGenerator(schema, "any/path/any-version.json", generatorOptionsJson: generatorOptions);

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "InitializeRequest"));
        Assert.Contains(result.GeneratedSources, source => source.Contains("public sealed partial class Implementation", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "SessionNotification"));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "SessionUpdate"));
        var jsonRenderer = Assert.Single(result.GeneratedSources.Where(source => source.Contains("internal static class GeneratedJson", StringComparison.Ordinal)));
        Assert.Contains("Serialize(InitializeRequest value)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("Serialize(PromptRequest value)", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("WriteContentBlock", jsonRenderer, StringComparison.Ordinal);
        Assert.Contains("WriteMcpServer", jsonRenderer, StringComparison.Ordinal);
        Assert.True(result.GeneratedSources.Length > 150);

        using var document = JsonDocument.Parse(schema);
        foreach (var definition in document.RootElement.GetProperty("$defs").EnumerateObject())
        {
            if (!definition.Value.TryGetProperty("x-method", out _) ||
                definition.Value.TryGetProperty("x-docs-ignore", out var docsIgnore) && docsIgnore.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            Assert.Contains(result.GeneratedSources, source => ContainsType(source, definition.Name));
        }
    }

    private static (ImmutableArray<string> GeneratedSources, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(
        string schema,
        string path,
        string? supportSource = null,
        string? generatorOptionsJson = null)
    {
        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();
        additionalTexts.Add(new TestAdditionalText(path, schema));
        if (generatorOptionsJson is not null)
            additionalTexts.Add(new TestAdditionalText("fixtures/acp.generator.json", generatorOptionsJson));
        var driver = CreateDriver(additionalTexts.ToImmutable());
        driver = driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(supportSource), out var outputCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        var sources = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToImmutableArray();
        return (sources, diagnostics.AddRange(outputCompilation.GetDiagnostics()));
    }

    private static bool ContainsType(string source, string name) =>
        source.Contains("class " + name, StringComparison.Ordinal) ||
        source.Contains("union " + name + "(", StringComparison.Ordinal) ||
        source.Contains("enum " + name, StringComparison.Ordinal);

    private static GeneratorDriver CreateDriver(ImmutableArray<AdditionalText> additionalTexts, bool useSystemTextJsonContext = false) =>
        CSharpGeneratorDriver.Create(
            generators: [new SchemaGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: useSystemTextJsonContext ? ["ACP_STJ_SOURCE_GEN"] : []));

    private static CSharpCompilation CreateCompilation(string? supportSource = null)
    {
        var platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var source = CSharpSyntaxTree.ParseText("global using System.Collections.Generic;", parseOptions);
        var syntaxTrees = supportSource is null
            ? new[] { source }
            : new[] { source, CSharpSyntaxTree.ParseText(supportSource, parseOptions) };
        return CSharpCompilation.Create(
            assemblyName: "GeneratedProtocolModels.Tests",
            syntaxTrees: syntaxTrees,
            references: platformAssemblies,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private const string MinimalSchema = """
        { "$schema": "https://json-schema.org/draft/2020-12/schema", "$defs": { "Thing": { "type": "object", "properties": { "name": { "type": "string" } } } } }
        """;

    private sealed class TestAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
