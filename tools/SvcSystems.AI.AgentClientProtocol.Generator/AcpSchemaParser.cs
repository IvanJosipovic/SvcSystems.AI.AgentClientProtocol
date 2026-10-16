using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal sealed class AcpSchemaParseResult
{
    public AcpSchemaParseResult(OpenApiSchema? schema, IReadOnlyList<string> errors)
    {
        Schema = schema;
        Errors = errors;
    }

    public OpenApiSchema? Schema { get; }

    public IReadOnlyList<string> Errors { get; }
}

internal static class AcpSchemaParser
{
    public static AcpSchemaParseResult Parse(string jsonSchema)
    {
        try
        {
            // OpenAPI 3.1 mode is Microsoft's JSON Schema 2020-12 reader. This is the schema dialect,
            // not an ACP protocol version; the generator consumes whatever definitions are supplied.
            var schema = OpenApiModelFactory.Parse<OpenApiSchema>(
                jsonSchema,
                OpenApiSpecVersion.OpenApi3_1,
                new OpenApiDocument(),
                out var diagnostics,
                format: "json");

            var errors = diagnostics.Errors.Select(error => error.Message).ToList();
            if (schema is null && errors.Count == 0)
            {
                errors.Add("Microsoft.OpenApi returned no root JSON Schema.");
            }
            return new AcpSchemaParseResult(errors.Count == 0 ? schema : null, errors);
        }
        catch (Exception exception)
        {
            return new AcpSchemaParseResult(null, [exception.Message]);
        }
    }
}
