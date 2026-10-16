using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class AcpSchemaGenerator : IIncrementalGenerator
{
    private const string SchemaExtension = ".json";

    private static readonly DiagnosticDescriptor MissingSchema = new(
        "ACPGEN001", "JSON Schema AdditionalFile is missing",
        "Add the JSON Schema input as an AdditionalFile ending in '{0}'",
        "AcpSchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSchema = new(
        "ACPGEN002", "JSON Schema could not be parsed",
        "Microsoft.OpenApi could not parse the schema: {0}",
        "AcpSchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedSchema = new(
        "ACPGEN003", "JSON Schema construct is not supported by the generator",
        "Schema definition '{0}' contains an unsupported construct: {1}",
        "AcpSchemaGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var schemaInputs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(SchemaExtension, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => new SchemaInput(file.Path, file.GetText(cancellationToken)?.ToString()))
            .Collect();

        context.RegisterSourceOutput(schemaInputs, Generate);
    }

    private static void Generate(SourceProductionContext context, ImmutableArray<SchemaInput> inputs)
    {
        if (inputs.Length != 1 || inputs[0].Text is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSchema, Location.None, SchemaExtension));
            return;
        }

        var parseResult = AcpSchemaParser.Parse(inputs[0].Text!);
        if (parseResult.Schema is null)
        {
            foreach (var error in parseResult.Errors)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidSchema, Location.None, error));
            }
            return;
        }

        var model = new ProtocolSchemaModelBuilder(context, UnsupportedSchema).Build(parseResult.Schema);
        foreach (var source in ProtocolModelRenderer.Render(model))
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
