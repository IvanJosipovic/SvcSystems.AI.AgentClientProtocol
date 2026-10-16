using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal static class ModelRenderer
{
    private const string GeneratedNamespace = "SvcSystems.AI.AgentClientProtocol.Protocol.Generated";
    private const string SerializationNamespace = "SvcSystems.AI.AgentClientProtocol.Serialization";

    public static IReadOnlyList<GeneratedSource> Render(SchemaModel model)
    {
        var sources = new List<GeneratedSource>(model.Types.Count + 3);
        sources.AddRange(RenderModels(model));
        sources.AddRange(RenderInfrastructure(model));
        return sources;
    }

    public static IReadOnlyList<GeneratedSource> RenderModels(SchemaModel model, bool generateJsonContext = false)
    {
        var sources = new List<GeneratedSource>(model.Types.Count + 1);
        foreach (var type in model.Types)
        {
            sources.Add(new GeneratedSource(
                type.Name + ".g.cs",
                RenderType(type)));
        }
        if (generateJsonContext)
        {
            sources.Add(new GeneratedSource("JsonContext.g.cs", RenderJsonContext(model)));
        }
        return sources;
    }

    public static IReadOnlyList<GeneratedSource> RenderInfrastructure(SchemaModel model, bool useSystemTextJsonContext = false)
    {
        var sources = new List<GeneratedSource>(6)
        {
            new GeneratedSource("GeneratedJson.g.cs", WireJsonRenderer.Render(model, useSystemTextJsonContext)),
            new GeneratedSource("Protocol.Methods.g.cs", RenderMethods(model)),
            new GeneratedSource("Protocol.Discriminators.g.cs", RenderDiscriminators(model))
        };
        if (useSystemTextJsonContext)
        {
            sources.Add(new GeneratedSource("CallbackRoutes.g.cs", RenderCallbackRoutes(model)));
            if (new[] { "ContentBlock", "ToolCallContent", "McpServer", "McpServerStdio", "McpServerHttp", "McpServerSse" }
                .All(name => model.Types.Any(type => type.Name == name)))
            {
                sources.Add(new GeneratedSource("ClientAdapters.g.cs", ClientAdapterRenderer.Render(model)));
            }
        }
        return sources;
    }

    private static string RenderJsonContext(SchemaModel model)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .AppendLine("using System.Text.Json.Serialization;")
            .AppendLine()
            .Append("namespace ").Append(SerializationNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]");
        foreach (var type in model.Types.Where(type => WireJsonRenderer.IsSystemTextJsonSafe(model, type.Name)))
        {
            code.Append("[JsonSerializable(typeof(global::").Append(GeneratedNamespace).Append('.').Append(type.Name).AppendLine("))]");
        }
        code.AppendLine("internal partial class JsonContext : JsonSerializerContext");
        code.AppendLine("{");
        code.AppendLine("}");
        return code.ToString();
    }

    private static string RenderDiscriminators(SchemaModel model)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .Append("namespace ").Append(GeneratedNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("internal static class Discriminators")
            .AppendLine("{");
        foreach (var discriminator in model.Types.OfType<EnumModel>().Where(type => type.Name.EndsWith("Discriminator", StringComparison.Ordinal)))
        {
            code.Append("    internal static ").Append(discriminator.Name).Append(" Parse").Append(discriminator.Name).AppendLine("(string? value) => value switch")
                .AppendLine("    {");
            foreach (var value in discriminator.Values.Skip(1))
            {
                code.Append("        ").Append(SymbolDisplay.FormatLiteral(value, true)).Append(" => ")
                    .Append(discriminator.Name).Append('.').Append(Identifier(Pascal(value))).AppendLine(",");
            }
            code.Append("        _ => ").Append(discriminator.Name).Append('.').Append(Identifier(Pascal(discriminator.Values[0]))).AppendLine()
                .AppendLine("    };")
                .Append("    internal static string ToWireValue(").Append(discriminator.Name).Append(" value) => value switch").AppendLine()
                .AppendLine("    {");
            foreach (var value in discriminator.Values.Skip(1))
            {
                code.Append("        ").Append(discriminator.Name).Append('.').Append(Identifier(Pascal(value))).Append(" => ")
                    .Append(SymbolDisplay.FormatLiteral(value, true)).AppendLine(",");
            }
            code.AppendLine("        _ => throw new global::System.ArgumentOutOfRangeException(nameof(value))")
                .AppendLine("    };");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private static string RenderMethods(SchemaModel model)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .Append("namespace ").Append(GeneratedNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("internal static class Methods")
            .AppendLine("{");
        foreach (var method in model.Methods.Select(method => method.Name).Distinct(StringComparer.Ordinal))
        {
            var identifier = Identifier(Pascal(method));
            code.Append("    internal const string ").Append(identifier).Append(" = ").Append(SymbolDisplay.FormatLiteral(method, true)).AppendLine(";");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private static string RenderCallbackRoutes(SchemaModel model)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .AppendLine("using System;")
            .AppendLine("using System.Collections.Generic;")
            .AppendLine("using System.Text.Json;")
            .AppendLine("using System.Threading;")
            .AppendLine("using System.Threading.Tasks;")
            .AppendLine("using SvcSystems.AI.AgentClientProtocol.JsonRpc;")
            .AppendLine()
            .Append("namespace ").Append(SerializationNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("internal static class CallbackRoutes")
            .AppendLine("{")
            .AppendLine("    internal static void Register(JsonRpcConnection connection,")
            .AppendLine("        IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, ValueTask<byte[]>>> requests,")
            .AppendLine("        IReadOnlyDictionary<string, Func<JsonElement, ValueTask>> notifications)")
            .AppendLine("    {");
        foreach (var route in model.Methods.Where(item => item.Side == "client" && item.MessageType is "request" or "notification")
                     .GroupBy(item => (item.Name, item.MessageType)).Select(group => group.First()))
        {
            var method = "global::SvcSystems.AI.AgentClientProtocol.Protocol.Generated.Methods." + Identifier(Pascal(route.Name));
            if (route.MessageType == "request")
                code.Append("        if (requests.TryGetValue(").Append(method).AppendLine(", out var requestHandler_" + Identifier(Pascal(route.Name)) + "))")
                    .Append("            connection.RegisterRequestHandler(").Append(method).Append(", requestHandler_").Append(Identifier(Pascal(route.Name))).AppendLine(");");
            else
                code.Append("        if (notifications.TryGetValue(").Append(method).AppendLine(", out var notificationHandler_" + Identifier(Pascal(route.Name)) + "))")
                    .Append("            connection.RegisterNotificationHandler(").Append(method).Append(", notificationHandler_").Append(Identifier(Pascal(route.Name))).AppendLine(");");
        }
        return code.AppendLine("    }").AppendLine("}").ToString();
    }

    private static string RenderType(TypeModel type)
    {
        if (type is UnionModel union) return RenderUnion(union);

        var member = type switch
        {
            ClassModel classModel => RenderClass(classModel),
            EnumModel enumModel => RenderEnum(enumModel),
            _ => throw new InvalidOperationException("Unknown protocol model: " + type.GetType().Name)
        };

        var unit = SyntaxFactory.CompilationUnit()
            .AddUsings(
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Collections.Generic")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Text.Json")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Text.Json.Serialization")))
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(GeneratedNamespace)).AddMembers(member));
        return "// <auto-generated />\n#nullable enable\n" + unit.NormalizeWhitespace(eol: "\n").ToFullString();
    }

    private static MemberDeclarationSyntax RenderClass(ClassModel model)
    {
        var declaration = SyntaxFactory.ClassDeclaration(model.Name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        if (model.DiscriminatorName is not null)
        {
            declaration = declaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
            declaration = declaration.AddAttributeLists(AttributeList(
                "JsonPolymorphic",
                NameArgument("TypeDiscriminatorPropertyName", StringExpression(model.DiscriminatorName))));
            foreach (var item in model.DiscriminatorCases)
            {
                declaration = declaration.AddAttributeLists(AttributeList(
                    "JsonDerivedType",
                    TypeOfArgument(item.TypeName),
                    StringArgument(item.DiscriminatorValue)));
            }
        }
        else if (model.BaseType is not null)
        {
            declaration = declaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(model.BaseType)));
        }
        else
        {
            declaration = declaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }

        foreach (var property in model.Properties)
        {
            declaration = declaration.AddMembers(RenderProperty(property));
        }

        if (model.HasExtensionData)
        {
            var extensionProperty = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("Dictionary<string, JsonElement>?"),
                    "ExtensionData")
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithAttributeLists(SyntaxFactory.SingletonList(AttributeList("JsonExtensionData")))
                .WithAccessorList(InitAccessors());
            declaration = declaration.AddMembers(extensionProperty);
        }
        return declaration;
    }

    private static PropertyDeclarationSyntax RenderProperty(PropertyModel model)
    {
        var property = SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(model.TypeName), model.Identifier)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithAccessorList(InitAccessors())
            .WithAttributeLists(SyntaxFactory.SingletonList(AttributeList("JsonPropertyName", StringArgument(model.WireName))));
        if (model.ConstValue is not null)
        {
            return property
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(StringExpression(model.ConstValue)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        if (model.IsRequired)
        {
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.RequiredKeyword));
        }
        if (model.IsRequired && model.TypeName.EndsWith("?", StringComparison.Ordinal))
        {
            property = property.AddAttributeLists(AttributeList(
                "JsonIgnore",
                NameArgument("Condition", SyntaxFactory.ParseExpression("JsonIgnoreCondition.Never"))));
        }
        return property;
    }

    private static MemberDeclarationSyntax RenderEnum(EnumModel model)
    {
        var declaration = SyntaxFactory.EnumDeclaration(model.Name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAttributeLists(AttributeList("JsonConverter", TypeOfArgument("JsonStringEnumConverter<" + model.Name + ">")));

        foreach (var (value, index) in model.Values.Select((value, index) => (value, index)))
        {
            var enumName = Identifier(Pascal(value));
            if (string.IsNullOrEmpty(enumName)) enumName = "Value" + index;
            var member = SyntaxFactory.EnumMemberDeclaration(enumName)
                .AddAttributeLists(AttributeList("JsonStringEnumMemberName", StringArgument(value)));
            declaration = declaration.AddMembers(member);
        }
        return declaration;
    }

    private static string RenderUnion(UnionModel model)
    {
        // STJ's union analyzer requires an explicit classifier for ambiguous token
        // shapes. The wire serializer remains generated from the schema and handles
        // non-object unions with its exact schema mapping; this classifier enables
        // STJ metadata generation for the models and object-shaped unions.
        const string classifier = "[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]\n";
        var code = new StringBuilder(GeneratedHeaderText())
            .AppendLine("using System.Text.Json.Serialization;")
            .AppendLine()
            .Append("namespace ").Append(GeneratedNamespace).AppendLine(";")
            .AppendLine()
            .Append(classifier)
            .Append("public union ").Append(model.Name).Append('(').Append(string.Join(", ", model.Cases)).AppendLine(");");
        return code.ToString();
    }

    private static AccessorListSyntax InitAccessors() => SyntaxFactory.AccessorList(
        SyntaxFactory.List([
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))]));

    private static AttributeListSyntax AttributeList(string name, params AttributeArgumentSyntax[] arguments) =>
        SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.IdentifierName(name))
                .WithArgumentList(arguments.Length == 0
                    ? null
                    : SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments)))));

    private static ExpressionSyntax StringExpression(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    private static AttributeArgumentSyntax StringArgument(string value) => SyntaxFactory.AttributeArgument(StringExpression(value));

    private static AttributeArgumentSyntax NameArgument(string name, ExpressionSyntax expression) =>
        SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals(name), null, expression);

    private static AttributeArgumentSyntax TypeOfArgument(string typeName) =>
        SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeName)));

    private static string GeneratedHeaderText() => "// <auto-generated />\n// Generated from the supplied JSON Schema AdditionalFile.\n#nullable enable\n";

    private static string Pascal(string value) => string.Concat(value.Split(new[] { '_', '-', '.', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));

    private static string Identifier(string value)
    {
        var identifier = new string(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (identifier.Length == 0 || !char.IsLetter(identifier[0]) && identifier[0] != '_') identifier = "_" + identifier;
        return identifier;
    }
}
