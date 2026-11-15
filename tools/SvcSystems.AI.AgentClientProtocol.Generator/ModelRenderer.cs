using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SvcSystems.AI.AgentClientProtocol.Generator;

internal static class ModelRenderer
{
    public static IReadOnlyList<GeneratedSource> Render(SchemaModel model)
    {
        var options = SchemaGeneratorOptions.Empty;
        var sources = new List<GeneratedSource>(model.Types.Count + 3);
        sources.AddRange(RenderModels(model, options));
        sources.AddRange(RenderInfrastructure(model, options));
        return sources;
    }

    public static IReadOnlyList<GeneratedSource> RenderModels(SchemaModel model, SchemaGeneratorOptions options)
    {
        var sources = new List<GeneratedSource>(model.Types.Count + 2);
        var modelNamespaces = GetModelNamespaces(model, options);
        foreach (var type in model.Types)
        {
            var modelNamespace = GetModelNamespace(type, options);
            sources.Add(new GeneratedSource(
                type.Name + ".g.cs",
                RenderType(type, modelNamespace, modelNamespaces, model, options)));
        }
        if (model.Types.OfType<UnionModel>().Any(union => union.HasGeneratedClassifier))
        {
            sources.Add(new GeneratedSource(
                UnionClassifierRenderer.FactoryName(model, options) + ".g.cs",
                UnionClassifierRenderer.Render(model, options, modelNamespaces)));
        }
        if (options.GenerateJsonContext)
        {
            sources.Add(new GeneratedSource(options.JsonContextName + ".g.cs", RenderJsonContext(model, options, options.SerializationNamespace, options.JsonContextName)));
        }
        return sources;
    }

    public static IReadOnlyList<GeneratedSource> RenderInfrastructure(
        SchemaModel model,
        SchemaGeneratorOptions options,
        IReadOnlyList<(string Name, string Value)>? protocolMethods = null)
    {
        var sources = new List<GeneratedSource>(6);
        var modelNamespaces = GetModelNamespaces(model, options);
        if (modelNamespaces.Length > 1)
        {
            sources.Add(new GeneratedSource("ModelNamespaces.g.cs", RenderGlobalUsings(modelNamespaces)));
        }
        if (protocolMethods is { Count: > 0 } || protocolMethods is null && model.Methods.Count > 0)
        {
            var methods = protocolMethods ?? model.Methods.Select(method => (method.Name, method.Name)).ToArray();
            sources.Add(new GeneratedSource("Protocol.Methods.g.cs", RenderMethods(methods, options.ModelNamespace)));
        }
        if (model.Types.OfType<EnumModel>().Any(type => type.Name.EndsWith("Discriminator", StringComparison.Ordinal)))
            sources.Add(new GeneratedSource("Protocol.Discriminators.g.cs", RenderDiscriminators(model, options.ModelNamespace, modelNamespaces)));
        return sources;
    }

    private static string RenderJsonContext(SchemaModel model, SchemaGeneratorOptions options, string serializationNamespace, string jsonContextName)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .AppendLine("using System.Text.Json.Serialization;")
            .AppendLine()
            .Append("namespace ").Append(serializationNamespace).AppendLine(";")
            .AppendLine()
            .Append("[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, AllowOutOfOrderMetadataProperties = true");
        if (model.Types.OfType<UnionModel>().Any(union => union.HasGeneratedClassifier))
        {
            code.Append(", TypeClassifiers = new[] { typeof(")
                .Append(UnionClassifierRenderer.FactoryName(model, options))
                .Append(") }");
        }
        code.AppendLine(")]");
        foreach (var type in model.Types)
        {
            code.Append("[JsonSerializable(typeof(global::").Append(GetModelNamespace(type, options)).Append('.').Append(type.Name).AppendLine("))]");
        }
        code.Append("internal partial class ").Append(jsonContextName).AppendLine(" : JsonSerializerContext");
        code.AppendLine("{");
        code.AppendLine("}");
        return code.ToString();
    }

    private static string GetModelNamespace(TypeModel type, SchemaGeneratorOptions options) => type.Side switch
    {
        "client" when options.ClientNamespace is not null => options.ClientNamespace,
        "agent" when options.AgentNamespace is not null => options.AgentNamespace,
        _ => options.ModelNamespace
    };

    /// <summary>
    /// Orders the sibling model namespaces deterministically: shared models first, then the
    /// role-specific namespaces, then any additional namespace discovered on a type. The per-file
    /// <c>using</c> lists and <c>ModelNamespaces.g.cs</c> are derived from this order, so keeping it
    /// independent of <see cref="SchemaModel.Types"/> iteration order stops a schema reorder from
    /// rewriting every generated file.
    /// </summary>
    /// <remarks>
    /// Only namespaces that actually contain a type are returned. Seeding a role namespace that has
    /// no types would emit a <c>using</c> for a namespace that is never declared, which fails the
    /// consumer's compilation.
    /// </remarks>
    private static string[] GetModelNamespaces(SchemaModel model, SchemaGeneratorOptions options)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in model.Types) used.Add(GetModelNamespace(type, options));

        var namespaces = new List<string>(used.Count);
        Add(options.ModelNamespace);
        Add(options.ClientNamespace);
        Add(options.AgentNamespace);
        foreach (var extra in used.Where(candidate => !namespaces.Contains(candidate, StringComparer.Ordinal))
                     .OrderBy(candidate => candidate, StringComparer.Ordinal))
        {
            namespaces.Add(extra);
        }

        return [.. namespaces];

        void Add(string? candidate)
        {
            if (candidate is not null && used.Contains(candidate) && !namespaces.Contains(candidate, StringComparer.Ordinal))
            {
                namespaces.Add(candidate);
            }
        }
    }

    private static string RenderGlobalUsings(IReadOnlyList<string> namespaces)
    {
        var code = new StringBuilder(GeneratedHeaderText());
        foreach (var modelNamespace in namespaces)
        {
            code.Append("global using global::").Append(modelNamespace).AppendLine(";");
        }
        return code.ToString();
    }

    /// <summary>
    /// Emits the file-level usings that import the sibling model namespaces into a file-scoped
    /// namespace declaration. These usings are required in the generated sources: the model types are
    /// referenced unqualified across namespaces, and every generated file imports the sibling
    /// namespaces as well as the compilation-wide <c>global using</c> aliases emitted by
    /// <see cref="RenderGlobalUsings"/>. Emitting them here (rather than post-processing rendered
    /// text) keeps the import list part of the file's own structure.
    /// </summary>
    private static void AppendNamespaceUsings(StringBuilder code, IReadOnlyList<string> namespaces, string currentNamespace)
    {
        foreach (var modelNamespace in namespaces)
        {
            if (modelNamespace == currentNamespace) continue;
            code.Append("using global::").Append(modelNamespace).Append(';').Append('\n');
        }
    }

    private static string RenderDiscriminators(SchemaModel model, string modelNamespace, IReadOnlyList<string> namespaces)
    {
        var code = new StringBuilder(GeneratedHeaderText());
        AppendNamespaceUsings(code, namespaces, modelNamespace);
        code.Append("namespace ").Append(modelNamespace).AppendLine(";")
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
                .AppendLine("    };");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private static string RenderMethods(IEnumerable<(string Name, string Value)> methods, string modelNamespace)
    {
        var code = new StringBuilder(GeneratedHeaderText())
            .Append("namespace ").Append(modelNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("internal static class Methods")
            .AppendLine("{");
        var emittedMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            if (!emittedMethods.Add(method.Value)) continue;

            var identifier = Identifier(Pascal(method.Name));
            code.Append("    internal const string ").Append(identifier).Append(" = ").Append(SymbolDisplay.FormatLiteral(method.Value, true)).AppendLine(";");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private static string RenderType(TypeModel type, string modelNamespace, IReadOnlyList<string> namespaces, SchemaModel schemaModel, SchemaGeneratorOptions options)
    {
            if (type is UnionModel union) return RenderUnion(union, modelNamespace, namespaces, schemaModel, options);

        var member = type switch
        {
            ClassModel classModel => RenderClass(classModel, options),
            DictionaryModel dictionaryModel => RenderDictionary(dictionaryModel),
            EnumModel enumModel => RenderEnum(enumModel),
            _ => throw new InvalidOperationException("Unknown protocol model: " + type.GetType().Name)
        };

        var unit = SyntaxFactory.CompilationUnit();
        foreach (var namespaceName in namespaces)
        {
            if (namespaceName == modelNamespace) continue;
            unit = unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::" + namespaceName)));
        }
        unit = unit
            .AddUsings(
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Collections.Generic")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Text.Json")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Text.Json.Serialization")))
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(modelNamespace)).AddMembers(member));

        var normalized = unit.NormalizeWhitespace(eol: "\n");
        // NormalizeWhitespace collapses the blank line that conventionally follows a file-scoped
        // namespace declaration, so restore it as explicit trivia on the namespace semicolon.
        var namespaceDeclaration = normalized.Members.OfType<FileScopedNamespaceDeclarationSyntax>().Single();
        var semicolon = namespaceDeclaration.SemicolonToken;
        normalized = (CompilationUnitSyntax)normalized.ReplaceNode(
            (SyntaxNode)namespaceDeclaration,
            namespaceDeclaration.WithSemicolonToken(
                semicolon.WithTrailingTrivia(semicolon.TrailingTrivia.Add(SyntaxFactory.EndOfLine("\n")))));
        return GeneratedHeaderText() + normalized.ToFullString();
    }

    private static MemberDeclarationSyntax RenderDictionary(DictionaryModel model)
    {
        var declaration = SyntaxFactory.ClassDeclaration(model.Name)
            .AddModifiers(SyntaxFactory.Token(model.IsInternal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword))
            .AddBaseListTypes(SyntaxFactory.SimpleBaseType(
                SyntaxFactory.ParseTypeName($"Dictionary<{model.KeyType}, {model.ValueType}>")));
        return declaration.WithLeadingTrivia(SummaryTrivia(model.Description));
    }

    private static MemberDeclarationSyntax RenderClass(ClassModel model, SchemaGeneratorOptions options)
    {
        var declaration = SyntaxFactory.ClassDeclaration(model.Name)
            .AddModifiers(SyntaxFactory.Token(model.IsInternal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword));

        if (model.DiscriminatorName is not null)
        {
            var isClosedHierarchy = options.GenerateJsonContext && model.DiscriminatorCases.Count > 0;
            declaration = declaration.AddModifiers(SyntaxFactory.Token(
                isClosedHierarchy ? SyntaxKind.ClosedKeyword : SyntaxKind.AbstractKeyword));
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
        return declaration.WithLeadingTrivia(SummaryTrivia(model.Description));
    }

    private static PropertyDeclarationSyntax RenderProperty(PropertyModel model)
    {
        var accessorList = HasCollectionNullDefault(model)
            ? DefaultOnNullAccessors(model.TypeName)
            : InitAccessors();
        var property = SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(model.TypeName), model.Identifier)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithAccessorList(accessorList)
            .WithAttributeLists(SyntaxFactory.SingletonList(AttributeList("JsonPropertyName", StringArgument(model.WireName))));
        if (model.IsIgnored)
        {
            property = property.AddAttributeLists(AttributeList("JsonIgnore"));
        }
        if (model.ConstValue is not null)
        {
            return property
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(StringExpression(model.ConstValue)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(SummaryTrivia(model.Description));
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
        return property.WithLeadingTrivia(SummaryTrivia(model.Description));
    }

    private static bool HasCollectionNullDefault(PropertyModel model)
    {
        var typeName = model.TypeName.TrimEnd('?');
        return model.DefaultOnNull &&
            (typeName.StartsWith("List<", StringComparison.Ordinal) ||
             typeName.StartsWith("Dictionary<", StringComparison.Ordinal));
    }

    private static MemberDeclarationSyntax RenderEnum(EnumModel model)
    {
        var declaration = SyntaxFactory.EnumDeclaration(model.Name)
            .AddModifiers(SyntaxFactory.Token(model.IsInternal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword))
            .AddAttributeLists(AttributeList("JsonConverter", TypeOfArgument("JsonStringEnumConverter<" + model.Name + ">")));

        foreach (var (value, index) in model.Values.Select((value, index) => (value, index)))
        {
            var enumName = Identifier(Pascal(value));
            if (string.IsNullOrEmpty(enumName)) enumName = "Value" + index;
            var member = SyntaxFactory.EnumMemberDeclaration(enumName)
                .AddAttributeLists(AttributeList("JsonStringEnumMemberName", StringArgument(value)));
            declaration = declaration.AddMembers(member);
        }
        return declaration.WithLeadingTrivia(SummaryTrivia(model.Description));
    }

    private static string RenderUnion(UnionModel model, string modelNamespace, IReadOnlyList<string> namespaces, SchemaModel schemaModel, SchemaGeneratorOptions options)
    {
        var code = new StringBuilder(GeneratedHeaderText());
        AppendNamespaceUsings(code, namespaces, modelNamespace);
        if (model.Cases.Contains("JsonElement", StringComparer.Ordinal))
            code.AppendLine("using System.Text.Json;");
        code.AppendLine("using System.Text.Json.Serialization;")
            .AppendLine()
            .Append("namespace ").Append(modelNamespace).AppendLine(";")
            .AppendLine();
        AppendSummary(code, model.Description);
        if (model.UsesStructuralClassifier)
        {
            code.AppendLine("[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]");
        }
        else if (model.HasGeneratedClassifier)
        {
            if (options.GenerateJsonContext)
            {
                code.AppendLine("[JsonUnion]");
            }
            else
            {
                code.Append("[JsonUnion(TypeClassifier = typeof(global::")
                    .Append(options.SerializationNamespace).Append('.').Append(UnionClassifierRenderer.FactoryName(schemaModel, options))
                    .AppendLine("))]");
            }
        }
        code.Append(model.IsInternal ? "internal union " : "public union ").Append(model.Name).Append('(').Append(string.Join(", ", model.Cases)).AppendLine(");");
        return code.ToString();
    }

    private static AccessorListSyntax InitAccessors() => SyntaxFactory.AccessorList(
        SyntaxFactory.List([
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))]));

    private static AccessorListSyntax DefaultOnNullAccessors(string typeName)
    {
        var collectionType = typeName.TrimEnd('?');
        var defaultValue = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(collectionType))
            .WithArgumentList(SyntaxFactory.ArgumentList());
        var valueOrDefault = SyntaxFactory.ConditionalExpression(
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("value"),
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
            defaultValue,
            SyntaxFactory.IdentifierName("value"));
        var initAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("field"),
                    valueOrDefault)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return SyntaxFactory.AccessorList(SyntaxFactory.List([
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            initAccessor]));
    }

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

    private static string GeneratedHeaderText() => "// <auto-generated />\n#pragma warning disable CS1591\n#nullable enable\n";

    private static SyntaxTriviaList SummaryTrivia(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return default;

        var code = new StringBuilder("/// <summary>\n");
        foreach (var line in description!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            code.Append("/// ").Append(EscapeXml(line)).AppendLine();
        code.AppendLine("/// </summary>");
        return SyntaxFactory.ParseLeadingTrivia(code.ToString());
    }

    private static void AppendSummary(StringBuilder code, string? description, string indent = "")
    {
        if (string.IsNullOrWhiteSpace(description)) return;

        code.Append(indent).AppendLine("/// <summary>");
        foreach (var line in description!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            code.Append(indent).Append("/// ").Append(EscapeXml(line)).AppendLine();
        code.Append(indent).AppendLine("/// </summary>");
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string Pascal(string value) => string.Concat(value.Split(new[] { '_', '-', '.', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));

    private static string Identifier(string value)
    {
        var identifier = new string(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (identifier.Length == 0 || !char.IsLetter(identifier[0]) && identifier[0] != '_') identifier = "_" + identifier;
        return identifier;
    }
}
