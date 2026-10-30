using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Rivet.Tool.Analysis;

/// <summary>
/// An MVC action's route values, read the way MVC's application model reads them, and the
/// attribute route with its <c>[token]</c>s replaced by them. <c>[controller]</c> is the
/// controller's name less its "Controller" suffix and <c>[action]</c> is the action's name.
/// Every other token takes the value an <c>IRouteValueProvider</c> attribute supplies:
/// <c>[Area]</c>, or any <c>RouteValueAttribute</c> subclass, on the action (or a method it
/// overrides) or on its controller (or a base class), the action's winning.
/// </summary>
internal static class MvcRouteValues
{
    /// <summary>
    /// The controller's name as MVC gives it: the class name less a "Controller" suffix,
    /// matched ignoring case.
    /// </summary>
    public static string ControllerName(INamedTypeSymbol type) =>
        type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? type.Name[..^"Controller".Length]
            : type.Name;

    /// <summary>
    /// <paramref name="route"/> with its tokens replaced and its doubled brackets unescaped,
    /// by MVC's own <see cref="AttributeRouteModel.ReplaceTokens(string, IDictionary{string, string})"/>,
    /// so an MVC route spells a regex class <c>[[a-z]]</c> and routing reads <c>[a-z]</c>.
    /// A route MVC refuses at startup (a token nothing supplies a value for, an unclosed
    /// bracket, or two route values with one key on the same action or controller) stops
    /// generation with <c>RIV1125</c>, as a template routing refuses does. So does a token
    /// only an attribute Rivet can't read could supply, saying that's why.
    /// </summary>
    public static string ReplaceTokens(
        WellKnownTypes wkt,
        string route,
        IMethodSymbol method,
        string endpoint
    )
    {
        var action = Level(wkt, Chain(method), $"action '{endpoint}'", route, endpoint);
        var controller = Level(
            wkt,
            method.ContainingType.SelfAndBases(),
            $"controller '{method.ContainingType.Name}'",
            route,
            endpoint
        );

        var values = new TokenValues
        {
            ["controller"] = ControllerName(method.ContainingType),
            ["action"] = ActionName(wkt, method),
        };
        foreach (var (key, value) in action.Values)
        {
            values.TryAdd(key, value);
        }
        // A provider on the action Rivet can't read might supply any key, and the action's
        // value beats the controller's, so the controller's values stand only without one.
        if (action.Unreadable.Count == 0)
        {
            foreach (var (key, value) in controller.Values)
            {
                values.TryAdd(key, value);
            }
        }

        try
        {
            var replaced = AttributeRouteModel.ReplaceTokens(route, values);
            if (values.Missing.Count == 0)
            {
                return replaced;
            }

            var unreadable = action.Unreadable.Concat(controller.Unreadable).Distinct().ToArray();
            if (unreadable.Length > 0)
            {
                throw Diagnostics.Error(
                    Diagnostics.RouteConstraintContradictsType,
                    $"route template '{route}' on endpoint '{endpoint}': Rivet can't read the route "
                        + $"value {string.Join(", ", values.Missing.Distinct(StringComparer.OrdinalIgnoreCase).Select(token => $"'[{token}]'"))}, "
                        + $"which only {string.Join(" or ", unreadable.Select(name => $"'{name}'"))} "
                        + "could supply. Rivet follows a route value attribute's constructor arguments "
                        + "to RouteValueAttribute's key and value, and they have to be constants on "
                        + "the way. Pass constants, or write the segment into the template."
                );
            }

            // Nothing supplies the token, so MVC's own lookup, over the values it would have,
            // throws its startup refusal.
            return AttributeRouteModel.ReplaceTokens(
                route,
                new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase)
            );
        }
        catch (InvalidOperationException exception)
        {
            throw Refused(route, endpoint, exception.Message);
        }
    }

    // The action's name: an [ActionName] on it or a method it overrides, else the method's
    // name less an "Async" suffix (MvcOptions.SuppressAsyncSuffixInActionNames, on by default).
    private static string ActionName(WellKnownTypes wkt, IMethodSymbol method) =>
        Chain(method)
            .Select(symbol => symbol.GetAttribute(wkt.ActionName).StringArgument())
            .FirstOrDefault(name => name is not null)
        ?? (
            method.Name.EndsWith("Async", StringComparison.Ordinal)
                ? method.Name[..^"Async".Length]
                : method.Name
        );

    private static IEnumerable<ISymbol> Chain(IMethodSymbol method) =>
        method.OverriddenChain().Prepend(method);

    /// <summary>
    /// The route values one level (the action, or the controller) supplies. Its attributes
    /// are what reflection's <c>GetCustomAttributes(inherit: true)</c> hands MVC
    /// (<see cref="RoslynExtensions.InheritedAttributes"/>). MVC adds each provider's value
    /// to one dictionary, so two with one key throw at startup.
    /// </summary>
    private static (
        IReadOnlyList<(string Key, string? Value)> Values,
        IReadOnlyList<string> Unreadable
    ) Level(
        WellKnownTypes wkt,
        IEnumerable<ISymbol> chain,
        string where,
        string route,
        string endpoint
    )
    {
        var values = new List<(string Key, string? Value, string From)>();
        var unreadable = new List<string>();
        foreach (var (attribute, _) in chain.InheritedAttributes(wkt.AttributeUsage))
        {
            if (
                attribute.AttributeClass is not { } attributeClass
                || !attributeClass.Implements(wkt.RouteValueProvider)
            )
            {
                continue;
            }

            if (Supplied(wkt, attribute) is { } supplied)
            {
                values.Add((supplied.Key, supplied.Value, attributeClass.Name));
            }
            else
            {
                unreadable.Add(attributeClass.Name);
            }
        }

        if (
            values
                .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1) is
            { } clash
        )
        {
            throw Refused(
                route,
                endpoint,
                $"two route values keyed '{clash.Key}' on the {where}, from "
                    + string.Join(" and ", clash.Select(value => value.From))
            );
        }

        return (values.Select(value => (value.Key, value.Value)).ToArray(), unreadable);
    }

    private static RivetUserException Refused(string route, string endpoint, string reason) =>
        Diagnostics.Error(
            Diagnostics.RouteConstraintContradictsType,
            $"route template '{route}' on endpoint '{endpoint}': MVC refuses it at startup "
                + $"({reason.TrimEnd('.')}). Fix the template."
        );

    /// <summary>
    /// The key and value a route value attribute supplies, followed from the arguments it's
    /// written with through its constructors to <c>RouteValueAttribute(key, value)</c> (or
    /// <c>AreaAttribute(area)</c>). Null when a step isn't a constant or one of the
    /// constructor's own parameters, or the attribute implements the interface itself.
    /// </summary>
    private static (string Key, string? Value)? Supplied(
        WellKnownTypes wkt,
        AttributeData attribute
    )
    {
        if (attribute.AttributeConstructor is not { } constructor)
        {
            return null;
        }

        var arguments = attribute
            .ConstructorArguments.Select(argument =>
                argument.Kind == TypedConstantKind.Array ? null : argument.Value
            )
            .ToArray();
        return Follow(wkt, constructor, arguments, depth: 0);
    }

    private static (string Key, string? Value)? Follow(
        WellKnownTypes wkt,
        IMethodSymbol constructor,
        object?[] arguments,
        int depth
    )
    {
        var type = constructor.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(type, wkt.RouteValueAttribute))
        {
            return arguments is [string key, var value] ? (key, value as string) : null;
        }

        if (SymbolEqualityComparer.Default.Equals(type, wkt.Area))
        {
            return arguments is [var area] ? ("area", area as string) : null;
        }

        if (depth > 16)
        {
            return null;
        }

        // A constructor the compiler wrote calls the base's parameterless one.
        if (constructor.IsImplicitlyDeclared && constructor.Parameters.IsEmpty)
        {
            return
                type.BaseType?.InstanceConstructors.FirstOrDefault(c => c.Parameters.IsEmpty)
                    is { } parameterless
                ? Follow(wkt, parameterless, [], depth + 1)
                : null;
        }

        if (Initializer(wkt.Compilation, constructor) is not { } initializer)
        {
            return null;
        }

        var passed = new object?[initializer.TargetMethod.Parameters.Length];
        foreach (var argument in initializer.Arguments)
        {
            if (argument.Parameter is not { } parameter)
            {
                return null;
            }

            var value = argument.Value;
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            if (value.ConstantValue.HasValue)
            {
                passed[parameter.Ordinal] = value.ConstantValue.Value;
            }
            else if (
                value is IParameterReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(
                    reference.Parameter.ContainingSymbol,
                    constructor
                )
                && reference.Parameter.Ordinal < arguments.Length
            )
            {
                passed[parameter.Ordinal] = arguments[reference.Parameter.Ordinal];
            }
            else
            {
                return null;
            }
        }

        return Follow(wkt, initializer.TargetMethod, passed, depth + 1);
    }

    // The base(...) or this(...) call a constructor with source opens with: a constructor's
    // initializer, or a primary constructor's base-type arguments.
    private static IInvocationOperation? Initializer(
        Compilation compilation,
        IMethodSymbol constructor
    )
    {
        if (constructor.DeclaringSyntaxReferences is not [var reference])
        {
            return null;
        }

        var syntax = reference.GetSyntax();
        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        var operation = syntax switch
        {
            ConstructorDeclarationSyntax declaration => (
                model.GetOperation(declaration) as IConstructorBodyOperation
            )?.Initializer,
            TypeDeclarationSyntax { BaseList.Types: var types } => types
                .OfType<PrimaryConstructorBaseTypeSyntax>()
                .Select(baseType => model.GetOperation(baseType))
                .FirstOrDefault(),
            _ => null,
        };
        while (operation is IExpressionStatementOperation statement)
        {
            operation = statement.Operation;
        }

        return operation as IInvocationOperation;
    }

    /// <summary>
    /// MVC fills a route's tokens through <see cref="IDictionary{TKey, TValue}.TryGetValue"/>.
    /// These values answer every token, keeping the ones they had no value for, so
    /// <see cref="ReplaceTokens"/> can tell a token only an unreadable attribute could supply
    /// from one nothing supplies, after MVC has checked the template's brackets.
    /// </summary>
    private sealed class TokenValues()
        : Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            IDictionary<string, string?>
    {
        public List<string> Missing { get; } = [];

        bool IDictionary<string, string?>.TryGetValue(string key, out string? value)
        {
            if (!TryGetValue(key, out value))
            {
                Missing.Add(key);
            }

            return true;
        }
    }
}
