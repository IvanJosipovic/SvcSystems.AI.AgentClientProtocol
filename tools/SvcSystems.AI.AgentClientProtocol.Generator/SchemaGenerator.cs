using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class SchemaGenerator : IIncrementalGenerator
{
    private const string SchemaExtension = ".json";

    private static readonly DiagnosticDescriptor MissingSchema = new(
        "ACPGEN001", "JSON Schema AdditionalFile is missing",
        "Add the JSON Schema input as an AdditionalFile ending in '{0}'",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSchema = new(
        "ACPGEN002", "JSON Schema could not be parsed",
        "Microsoft.OpenApi could not parse the schema: {0}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedSchema = new(
        "ACPGEN003", "JSON Schema construct is not supported by the generator",
        "Schema definition '{0}' contains an unsupported construct: {1}",
        "SchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var schemaInputs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(SchemaExtension, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => new SchemaInput(file.Path, file.GetText(cancellationToken)?.ToString()))
            .Collect();

        var generateJsonContext = context.ParseOptionsProvider
            .Select(static (options, _) => options is CSharpParseOptions csharpOptions &&
                csharpOptions.PreprocessorSymbolNames.Contains("ACP_STJ_SOURCE_GEN"));
        var generateClientAdapters = context.ParseOptionsProvider
            .Select(static (options, _) => options is CSharpParseOptions csharpOptions &&
                csharpOptions.PreprocessorSymbolNames.Contains("ACP_CLIENT_ADAPTERS"));
        context.RegisterPreCompilationSourceOutput(schemaInputs.Combine(generateJsonContext), GenerateModels);
        context.RegisterSourceOutput(schemaInputs.Combine(generateJsonContext).Combine(generateClientAdapters), Generate);
    }

    private static void GenerateModels(PreCompilationSourceProductionContext context, (ImmutableArray<SchemaInput> Inputs, bool GenerateJsonContext) input)
    {
        var inputs = input.Inputs;
        if (inputs.Length != 1 || inputs[0].Text is null) return;
        var parseResult = SchemaParser.Parse(inputs[0].Text!);
        if (parseResult.Schema is null) return;

        var model = new SchemaModelBuilder(null, UnsupportedSchema).Build(parseResult.Schema);
        foreach (var source in ModelRenderer.RenderModels(model, input.GenerateJsonContext))
        {
            context.AddSource(source.HintName, SourceText.From(source.Text, System.Text.Encoding.UTF8));
        }
    }

    private static void Generate(SourceProductionContext context, ((ImmutableArray<SchemaInput> Inputs, bool GenerateJsonContext) Input, bool GenerateClientAdapters) input)
    {
        var inputs = input.Input.Inputs;
        if (inputs.Length != 1 || inputs[0].Text is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSchema, Location.None, SchemaExtension));
            return;
        }

        var parseResult = SchemaParser.Parse(inputs[0].Text!);
        if (parseResult.Schema is null)
        {
            foreach (var error in parseResult.Errors)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidSchema, Location.None, error));
            }
            return;
        }

        var model = new SchemaModelBuilder(context.ReportDiagnostic, UnsupportedSchema).Build(parseResult.Schema);
        foreach (var source in ModelRenderer.RenderInfrastructure(model, input.Input.GenerateJsonContext, input.GenerateClientAdapters))
        {
            context.AddSource(source.HintName, SourceText.From(source.Text, System.Text.Encoding.UTF8));
        }
    }

    private sealed class SchemaInput(string path, string? text)
    {
        public string Path { get; } = path;
        public string? Text { get; } = text;
    }
}
