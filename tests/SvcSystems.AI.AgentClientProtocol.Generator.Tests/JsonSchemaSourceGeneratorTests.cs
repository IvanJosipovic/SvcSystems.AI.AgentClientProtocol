using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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
            additionalTexts: [
                new TestAdditionalText("protocol.schema.json", MinimalSchema),
                new TestAdditionalText("protocol.generator.json", "{ \"generateJsonContext\": true }")],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGenerators(CreateCompilation(), TestContext.Current.CancellationToken);
        var sources = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToArray();

        Assert.Contains(sources, source => source.Contains("internal partial class JsonContext : JsonSerializerContext", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Generated.Thing))", StringComparison.Ordinal));
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
        var jsonContext = Assert.Single(generatedSources, source => source.Contains("JsonSourceGenerationOptions", StringComparison.Ordinal));

        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Generated.Thing))", jsonContext, StringComparison.Ordinal);
        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Generated.Container))", jsonContext, StringComparison.Ordinal);
        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Generated.Choice))", jsonContext, StringComparison.Ordinal);
        Assert.DoesNotContain(generatedSources, source => source.Contains("GeneratedJson", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratesMultiplePairedSchemasIntoSeparateNamespaces()
    {
        var compilation = CreateCompilation();
        var driver = CreateDriver([
            new TestAdditionalText("one.schema.json", MinimalSchema),
            new TestAdditionalText("one.generator.json", "{ \"namespace\": \"Tests.One\" }"),
            new TestAdditionalText("two.schema.json", MinimalSchema),
            new TestAdditionalText("two.generator.json", "{ \"namespace\": \"Tests.Two\" }")]);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var sources = driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources).Select(source => source.SourceText.ToString()).ToArray();
        Assert.Contains(sources, source => source.Contains("namespace Tests.One;", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains("namespace Tests.Two;", StringComparison.Ordinal));
    }

    [Fact]
    public void RoutesSideTaggedModelsToClientAndAgentNamespacesAndLeavesSharedModelsInModelsNamespace()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "SharedModel": { "type": "object", "properties": { "id": { "type": "string" } } },
                "ClientModel": { "type": "object", "properties": { "path": { "type": "string" } }, "x-side": "client" },
                "AgentModel": { "type": "object", "properties": { "prompt": { "type": "string" } }, "x-side": "agent" }
              }
            }
            """;
        const string options = """
            {
              "namespace": "Tests.Models",
              "clientNamespace": "Tests.Client.Models",
              "agentNamespace": "Tests.Agent.Models"
            }
            """;

        var result = RunGenerator(schema, "roles.schema.json", generatorOptionsJson: options);

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, source => source.Contains("namespace Tests.Models;", StringComparison.Ordinal) &&
            source.Contains("class SharedModel", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("namespace Tests.Client.Models;", StringComparison.Ordinal) &&
            source.Contains("class ClientModel", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => source.Contains("namespace Tests.Agent.Models;", StringComparison.Ordinal) &&
            source.Contains("class AgentModel", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefixesGeneratedHintNamesWithSchemaNameWithoutChangingTypeNames()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "RegistryAgent": {
                  "type": "object",
                  "properties": { "id": { "type": "string" } }
                }
              }
            }
            """;
        var driver = CreateDriver([
            new TestAdditionalText("repo/schema/registry/v1/registry.schema.json", schema),
            new TestAdditionalText("repo/schema/registry/v1/registry.generator.json", "{ \"namespace\": \"Tests.Registry\" }")]);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(), out _, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generatedSources = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .ToArray();
        var model = Assert.Single(generatedSources, source => source.HintName == "registry.RegistryAgent.g.cs");
        Assert.Contains("partial class RegistryAgent", model.SourceText.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("partial class registry.RegistryAgent", model.SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsInvalidSchemaAsGeneratorDiagnostic()
    {
        var driver = CreateDriver([new TestAdditionalText("broken.schema.json", "{ this is not json")]);
        driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(), out _, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ACPGEN002");
    }

    [Fact]
    public void ReportsUnresolvedSchemaReferencesAsErrors()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "Request": {
                  "type": "object",
                  "properties": {
                    "payload": { "$ref": "#/$defs/MissingPayload" }
                  }
                }
              }
            }
            """;

        var result = RunGenerator(schema, "unresolved-reference.schema.json");

        var diagnostic = Assert.Single(result.Diagnostics.Where(item => item.Id == "ACPGEN003"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("unresolved schema reference", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsUnsupportedObjectShapesAsErrors()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "Request": {
                  "type": "object",
                  "patternProperties": {
                    "^item": { "type": "integer" }
                  }
                }
              }
            }
            """;

        var result = RunGenerator(schema, "unsupported-object-shape.schema.json");

        var diagnostic = Assert.Single(result.Diagnostics.Where(item => item.Id == "ACPGEN003"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("patternProperties", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void IntentionallyUnconstrainedReferencesRemainJsonElements()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": {
                "Raw-Payload": {},
                "Request": {
                  "type": "object",
                  "properties": {
                    "payload": { "$ref": "#/$defs/Raw-Payload" }
                  }
                }
              }
            }
            """;

        var result = RunGenerator(schema, "open-payload.schema.json");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "ACPGEN003");
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("class Request", StringComparison.Ordinal) &&
            source.Contains("JsonElement? Payload", StringComparison.Ordinal));
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

        var result = RunGenerator(schema, "enum.schema.json", useSystemTextJsonContext: true);

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var enumSource = Assert.Single(result.GeneratedSources.Where(source => source.Contains("enum TaskState", StringComparison.Ordinal)));
        Assert.Contains("InProgress", enumSource, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"in-progress\")", enumSource, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"awaiting_input\")", enumSource, StringComparison.Ordinal);
        var jsonContext = Assert.Single(result.GeneratedSources.Where(source => source.Contains("JsonSourceGenerationOptions", StringComparison.Ordinal)));
        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Generated.TaskState))", jsonContext, StringComparison.Ordinal);
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
        var result = RunGenerator(schema, "any/path/any-version.json", supportSource: JsonRpcConnectionSupportSource,
            generatorOptionsJson: generatorOptions, useSystemTextJsonContext: true);

        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "InitializeRequest"));
        Assert.Contains(result.GeneratedSources, source => source.Contains("public sealed partial class Implementation", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "SessionNotification"));
        Assert.Contains(result.GeneratedSources, source => ContainsType(source, "SessionUpdate"));
        var jsonContext = Assert.Single(result.GeneratedSources.Where(source => source.Contains("JsonSourceGenerationOptions", StringComparison.Ordinal)));
        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Agent.Models.InitializeRequest))", jsonContext, StringComparison.Ordinal);
        Assert.Contains("JsonSerializable(typeof(global::SvcSystems.AI.AgentClientProtocol.Agent.Models.PromptRequest))", jsonContext, StringComparison.Ordinal);
        Assert.DoesNotContain(result.GeneratedSources, source => source.Contains("GeneratedJson", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("namespace SvcSystems.AI.AgentClientProtocol.Client.Models;", StringComparison.Ordinal) &&
            source.Contains("class WriteTextFileRequest", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("namespace SvcSystems.AI.AgentClientProtocol.Models;", StringComparison.Ordinal) &&
            source.Contains("class SessionUpdate", StringComparison.Ordinal));
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
        string? generatorOptionsJson = null,
        bool useSystemTextJsonContext = false)
    {
        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();
        var schemaPath = EnsureSchemaSuffix(path);
        additionalTexts.Add(new TestAdditionalText(schemaPath, schema));
        if (generatorOptionsJson is not null)
            additionalTexts.Add(new TestAdditionalText(GetOptionsPath(schemaPath), generatorOptionsJson));
        else if (useSystemTextJsonContext)
            additionalTexts.Add(new TestAdditionalText(GetOptionsPath(schemaPath), "{ \"generateJsonContext\": true }"));
        var driver = CreateDriver(additionalTexts.ToImmutable(), useSystemTextJsonContext);
        driver = driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(supportSource), out var outputCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        var sources = driver.GetRunResult().Results
            .Where(result => result.Generator.GetGeneratorType() == typeof(SchemaGenerator))
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToImmutableArray();
        return (sources, diagnostics.AddRange(outputCompilation.GetDiagnostics()));
    }

    private static bool ContainsType(string source, string name) =>
        source.Contains("class " + name, StringComparison.Ordinal) ||
        source.Contains("union " + name + "(", StringComparison.Ordinal) ||
        source.Contains("enum " + name, StringComparison.Ordinal);

    private static GeneratorDriver CreateDriver(ImmutableArray<AdditionalText> additionalTexts, bool useSystemTextJsonContext = false)
    {
        if (useSystemTextJsonContext)
        {
            var optionsPaths = additionalTexts
                .Where(item => item.Path.EndsWith(".generator.json", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var withOptions = additionalTexts.ToBuilder();
            foreach (var schema in additionalTexts.Where(item => item.Path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)))
            {
                var optionsPath = GetOptionsPath(schema.Path);
                if (!optionsPaths.Contains(optionsPath))
                    withOptions.Add(new TestAdditionalText(optionsPath, "{ \"generateJsonContext\": true }"));
            }
            additionalTexts = withOptions.ToImmutable();
        }

        return CSharpGeneratorDriver.Create(
            generators: useSystemTextJsonContext
                ? [new SchemaGenerator().AsSourceGenerator(), new System.Text.Json.SourceGeneration.JsonSourceGenerator().AsSourceGenerator()]
                : [new SchemaGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
    }

    private static string EnsureSchemaSuffix(string path)
    {
        if (path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)) return path;
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? path[..^".json".Length] + ".schema.json"
            : path + ".schema.json";
    }

    private static string GetOptionsPath(string schemaPath) =>
        schemaPath[..^".schema.json".Length] + ".generator.json";

    private static CSharpCompilation CreateCompilation(string? supportSource = null)
    {
        var platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            // The generator embeds JSON enum types that conflict with the runtime assembly.
            .Where(path => !string.Equals(Path.GetFileName(path), "System.Text.Json.SourceGeneration.dll", StringComparison.OrdinalIgnoreCase))
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

    // Supplies the callback registration contract for compile-only generator tests.
    private const string JsonRpcConnectionSupportSource = """
        namespace SvcSystems.AI.AgentClientProtocol.JsonRpc
        {
            using System;
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;

            internal sealed class JsonRpcConnection
            {
                public void RegisterRequestHandler(string method, Func<JsonElement, CancellationToken, ValueTask<byte[]>> handler)
                    => throw new NotSupportedException();

                public void RegisterNotificationHandler(string method, Func<JsonElement, ValueTask> handler)
                    => throw new NotSupportedException();
            }
        }
        """;

    private sealed class TestAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
