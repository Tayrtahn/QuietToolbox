using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Robust.CompletionProviders;

[ExportCompletionProvider(nameof(DependencyCompletionProvider), LanguageNames.CSharp)]
public sealed class DependencyCompletionProvider : CompletionProvider
{
    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        var document = context.Document;

        if (!document.SupportsSemanticModel || !document.SupportsSyntaxTree)
            return;

        var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var token = root.FindToken(context.Position);

        var memberSyntax = token.Parent?.AncestorsAndSelf().OfType<IncompleteMemberSyntax>().FirstOrDefault();

        if (memberSyntax is null || memberSyntax.Type is null)
            return;

        var spaceCount = memberSyntax.ToString().Count(c => c == ' ');
        if (spaceCount < 2)
            return;

        if (!HasDependencyAttribute(memberSyntax))
            return;

        var isPrivate = memberSyntax.Modifiers.Any(SyntaxKind.PrivateKeyword);

        var fieldName = GetDependencyFieldName(memberSyntax.Type, isPrivate);

        var model = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var typeSymbol = model?.GetSymbolInfo(memberSyntax.Type).Symbol as ITypeSymbol;
        var nullSuppression = DefaultNeedsNullSuppression(typeSymbol) ? "!" : "";

        var suggestion = $"{fieldName} = default{nullSuppression};";

        var completion = CompletionItem.Create(
            suggestion,
            suggestion,
            suggestion,
            rules: CompletionItemRules.Default.WithMatchPriority(MatchPriority.Preselect)
        );

        context.AddItem(completion);
        //context.IsExclusive = true;
    }

    private static bool HasDependencyAttribute(IncompleteMemberSyntax node)
    {
        foreach (var attrList in node.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                if (attr.Name.ToString() == "Dependency")
                    return true;
            }
        }
        return false;
    }

    private static bool DefaultNeedsNullSuppression(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
            return true;

        return typeSymbol.IsReferenceType;
    }

    private static readonly Dictionary<string, string> SpecialNameMappings = new()
    {
        {"EntityManager", "EntMan"},
        {"PrototypeManager", "ProtoMan"},
        {"TransformSystem", "Xform"},
    };

    private static string GetDependencyFieldName(TypeSyntax typeSyntax, bool isPrivate)
    {
        var typeName = typeSyntax.ToString();

        // Special case for EntityQuery - transform "EntityQuery<MyThingComponent>" to "MyThingQuery".
        if (typeName.StartsWith("EntityQuery<"))
        {
            // Get the substring containing the name of the component the query is for.
            var compName = typeName.Substring("EntityQuery<".Length, typeName.Length - "EntityQuery<".Length - 1);

            // Remove trailing "Component" from component name.
            if (compName.EndsWith("Component"))
                compName = compName.Substring(0, compName.Length - "Component".Length);

            typeName = $"{compName}Query";
        }

        // Remove leading 'I' from interface names.
        if (typeName[0] == 'I' && typeName.Length > 1 && char.IsUpper(typeName[1]))
            typeName = typeName.Substring(1);

        // Remove leading side prefixes.
        foreach (var prefix in new string[] {"Shared", "Server", "Client"})
        {
            if (typeName.StartsWith(prefix))
                typeName = typeName.Substring(prefix.Length);
        }

        // Check for a special match.
        if (SpecialNameMappings.TryGetValue(typeName, out var special))
            typeName = special;

        // Remove trailing 'System' from EntitySystem names.
        if (typeName.EndsWith("System"))
            typeName = typeName.Substring(0, typeName.Length - "System".Length);

        if (isPrivate)
        {
            // Add leading '_' and change first letter to lowercase.
            return $"_{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}";
        }
        else
        {
            return typeName;
        }
    }
}
