using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;

namespace Robust.CompletionProviders;

/// <summary>
/// Provides completion suggestions when typing a new [Dependency] field.
/// Suggestion is provided after the Type has been specified, <c>[Dependency] private IEntityManager _</c>.
/// The completion automatically provides a name for the field and sets the value to <see langword="default"/>
/// with null suppression if needed.
/// </summary>
[ExportCompletionProvider(nameof(DependencyCompletionProvider), LanguageNames.CSharp)]
public sealed class DependencyCompletionProvider : CompletionProvider
{
    /// <summary>
    /// Mapping of special cases of name suggestions for specific types.
    /// Key is the triggering typename without the leading 'I' for interfaces or any side prefix (like 'Shared' or 'Server').
    /// Value is the resulting name suggestion, formatted as public/protected field name (PascalCase rather than _privateField).
    /// </summary>
    private static readonly Dictionary<string, string> SpecialNameMappings = new()
    {
        {"EntityManager", "EntMan"},
        {"PrototypeManager", "ProtoMan"},
        {"ConfigurationManager", "Cfg"},
        {"GameTiming", "Timing"},
    };

    private const string DependencyAttributeName = "Dependency";
    private const string EntityQueryName = "EntityQuery";
    private const string ComponentSuffix = "Component";
    private const string SystemSuffix = "System";
    private readonly static string[] SidePrefixes = ["Shared", "Server", "Client"];

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        var document = context.Document;

        if (!document.SupportsSemanticModel || !document.SupportsSyntaxTree)
            return;

        var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        // Get the syntax token at the cursor position.
        var token = root.FindToken(context.Position);

        // Since the user is still typing the line, we don't know if it's a field, property, method, or whatever.
        // So we get the incomplete syntax and deduce what we can.
        var memberSyntax = token.Parent?.AncestorsAndSelf().OfType<IncompleteMemberSyntax>().FirstOrDefault();

        // If the cursor isn't in some other sort of code, memberSyntax will be null.
        // If they've typed 'private' but not the field type yet, memberSyntax.Type will be null.
        if (memberSyntax?.Type is null)
            return;

        // Don't offer completions until the user specifies private/public.
        // This prevents showing suggestions too early, like
        // "[Dependency] priva" -> "[Dependency] _priva = default!".
        if (memberSyntax.Modifiers.Count < 1)
            return;

        // We only want to offer completions on [Dependency] fields.
        if (!HasDependencyAttribute(memberSyntax))
            return;

        // Private fields have a different expected name format.
        if (!TryGetAccessiblityModifier(memberSyntax, out var fieldAccessibility))
            return;

        // Get the suggested field name.
        var fieldName = GetDependencyFieldName(memberSyntax.Type, fieldAccessibility.Value);

        // Look up the symbol for the field's Type.
        // We don't do this earlier because it's fairly expensive.
        var model = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var typeSymbol = model?.GetSymbolInfo(memberSyntax.Type).Symbol as ITypeSymbol;

        // Evaluate the type to see if we need to add null supression (!) to the assignment.
        var nullSuppression = DefaultNeedsNullSuppression(typeSymbol) ? "!" : "";

        // Construct the final text of the suggestion.
        var suggestion = $"{fieldName} = default{nullSuppression};";

        var completion = CompletionItem.Create(
            suggestion,
            suggestion,
            suggestion,
            rules: CompletionItemRules.Default.WithMatchPriority(MatchPriority.Preselect),
            tags: [WellKnownTags.Snippet]
        );

        context.AddItem(completion);
    }

    /// <summary>
    /// Attempts to get the accessibility modifier (private, protected, etc.) of a syntax node.
    /// </summary>
    /// <remarks>
    /// Because this uses syntax instead of semantic analysis, it only detects explicit modifiers.
    /// Implicit modifiers will not be found.
    /// </remarks>
    private static bool TryGetAccessiblityModifier(MemberDeclarationSyntax node, [NotNullWhen(true)] out Accessibility? accessibility)
    {
        if (node.Modifiers.Any(SyntaxKind.PublicKeyword))
        {
            accessibility = Accessibility.Public;
            return true;
        }
        if (node.Modifiers.Any(SyntaxKind.ProtectedKeyword))
        {
            accessibility = Accessibility.Protected;
            return true;
        }
        if (node.Modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            accessibility = Accessibility.Private;
            return true;
        }
        if (node.Modifiers.Any(SyntaxKind.InternalKeyword))
        {
            accessibility = Accessibility.Internal;
            return true;
        }

        accessibility = null;
        return false;
    }

    public override async Task<CompletionDescription?> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
    {
        return CompletionDescription.FromText("(Auto-complete dependency field)");
    }

    /// <summary>
    /// Checks if <paramref name="node"/> has the [Dependency] attribute.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if <paramref name="node"/> has the [Dependency] attribute, otherwise <see langword="false"/>.
    /// </returns>
    private static bool HasDependencyAttribute(IncompleteMemberSyntax node)
    {
        foreach (var attrList in node.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                if (attr.Name.ToString() == DependencyAttributeName)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether a field of the specified type needs nullable suppression (!) in order to assign <see langword="null"/> to it.
    /// </summary>
    /// <returns><see langword="true"/> if the assignment requires nullable suppression, otherwise <see langword="false"/>.</returns>
    private static bool DefaultNeedsNullSuppression(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
            return true;

        return typeSymbol.IsReferenceType;
    }

    /// <summary>
    /// Generates a suggested name for a dependency field of the specified type.
    /// </summary>
    /// <param name="typeSyntax">The Type of the dependency field.</param>
    /// <param name="isPrivate">
    ///     The accessiblity of the field, such as private or protected.
    ///     Determines the format of the returned name.
    /// </param>
    /// <returns>The suggested name.</returns>
    /// <seealso cref="SpecialNameMappings"/>
    private static string GetDependencyFieldName(TypeSyntax typeSyntax, Accessibility accessibility)
    {
        // If the type has a nullable annotation (?), remove it.
        if (typeSyntax is NullableTypeSyntax nullableSyntax)
            typeSyntax = nullableSyntax.ElementType;

        var typeName = typeSyntax is GenericNameSyntax genericSyntax
            ? GetGenericFieldName(genericSyntax)
            : RemoveLeadingIFromInterface(typeSyntax.ToString());

        // Remove leading side prefixes.
        foreach (var prefix in SidePrefixes)
        {
            if (typeName.StartsWith(prefix))
                typeName = typeName.Substring(prefix.Length);
        }

        // Check for a special match.
        if (SpecialNameMappings.TryGetValue(typeName, out var special))
            typeName = special;

        // Remove trailing 'System' from EntitySystem names.
        if (typeName.EndsWith(SystemSuffix))
            typeName = typeName.Substring(0, typeName.Length - SystemSuffix.Length);

        return accessibility switch
        {
            // Add leading '_' and change first letter to lowercase.
            Accessibility.Private or Accessibility.Internal => $"_{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}",
            // Just use the name.
            _ => typeName,
        };
    }

    private static string RemoveLeadingIFromInterface(string typeName)
    {
        if (typeName[0] == 'I' && typeName.Length > 1 && char.IsUpper(typeName[1]))
            return typeName.Substring(1);
        return typeName;
    }

    /// <summary>
    /// Generates a suggested name for a dependency field with a generic type.
    /// </summary>
    /// <example>
    ///     SomeGeneric<SomeThing> -> SomeThingSomeGeneric
    /// </example>
    /// <example>
    ///     EntityQuery<SomeThingComponent> -> SomeThingQuery
    /// </example>
    /// <returns>The suggested name.</returns>
    private static string GetGenericFieldName(GenericNameSyntax genericSyntax)
    {
        var typeArgNames = genericSyntax.TypeArgumentList.Arguments.Select(GetTypeArgName);
        var outerTypeName = RemoveLeadingIFromInterface(genericSyntax.Identifier.ToString());

        // Special case for EntityQuery - transform "EntityQuery<MyThingComponent>" to "MyThingQuery".
        if (outerTypeName == EntityQueryName)
            outerTypeName = "Query";

        // Concatenate the type arg names and put the generic type name on the end.
        return $"{string.Join("", typeArgNames)}{outerTypeName}";
    }

    /// <summary>
    /// Generates a suggested name for a type argument to be included in a generic dependency field name.
    /// </summary>
    private static string GetTypeArgName(TypeSyntax arg)
    {
        var name = arg.ToString();

        // Remove trailing "Component" from component name.
        if (name.EndsWith(ComponentSuffix))
            name = name.Substring(0, name.Length - ComponentSuffix.Length);

        // Capitalize the first letter.
        return $"{char.ToUpperInvariant(name[0])}{name.Substring(1)}";
    }
}
