using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// The role-aware code rules. LEVEE003: no wall clock outside tests (TimeProvider.System only where the app composes). LEVEE004:
/// nothing saves, controls a transaction, or sets the scope outside tests and Tools: the dispatcher commits. LEVEE005: services
/// are located only where the app composes. LEVEE018: no connection of its own. LEVEE020: the catalog is the generator's.
/// LEVEE022: no SQL on the change signals' channel.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeRules : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> TransactionMembers = ImmutableHashSet.Create(
        "SaveChanges", "SaveChangesAsync", "BeginTransaction", "BeginTransactionAsync", "CommitTransaction", "CommitTransactionAsync",
        "RollbackTransaction", "RollbackTransactionAsync", "UseTransaction", "UseTransactionAsync");

    private static readonly ImmutableHashSet<string> TransactionObjectMembers = ImmutableHashSet.Create(
        "Commit", "CommitAsync", "Rollback", "RollbackAsync", "Save", "SaveAsync", "Release", "ReleaseAsync");

    private static readonly ImmutableHashSet<string> LocatorMembers = ImmutableHashSet.Create(
        "GetService", "GetRequiredService", "GetServices", "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices", "CreateScope",
        "CreateAsyncScope", "CreateInstance", "GetServiceOrCreateInstance", "CreateFactory");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.WallClock, Descriptors.Transaction, Descriptors.ScopeSetting, Descriptors.ServiceLocation, Descriptors.SideConnection, Descriptors.HandlerClock, Descriptors.HelperClock,
            Descriptors.SeparateContextReach, Descriptors.ByName, Descriptors.Reflected, Descriptors.SignalByHand, Descriptors.ProcessSends, Descriptors.PrincipalMade, Descriptors.PrincipalHeld,
            Descriptors.OwnerRun, Descriptors.CatalogByHand);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code is held to the rules too: a header is no way out. Levee's own output is skipped by its path.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var role = Roles.Of(start.Options);
            if (role == Role.None || Roles.IsTest(role) || !Roles.UsesLevee(start.Compilation))
            {
                return;
            }
            start.RegisterOperationAction(c => Known.Unless(c, () => OnProperty(c, role)), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnInvocation(c, role)), OperationKind.Invocation);
            // A method group is a call deferred: Func<Type, object?> f = services.GetService locates all the same.
            start.RegisterOperationAction(c => Known.Unless(c, () => OnMethodGroup(c, role)), OperationKind.MethodReference);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnCreation(c, role)), OperationKind.ObjectCreation);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnByName(c)), OperationKind.DynamicInvocation);
            start.RegisterSyntaxNodeAction(OnOperationAttributeNamed, SyntaxKind.IdentifierName);
            start.RegisterSymbolAction(OnProcess, SymbolKind.NamedType);
            start.RegisterSymbolAction(c => OnHandlerClock(c, role), SymbolKind.NamedType);
            if (role is Role.Kernel or Role.Contracts or Role.Domain or Role.Application or Role.Adapter)
            {
                start.RegisterOperationAction(c => Known.Unless(c, () => OnPrincipalMade(c, role)), OperationKind.ObjectCreation, OperationKind.With);
                start.RegisterSymbolAction(c => OnPrincipalHeld(c, role), SymbolKind.NamedType);
            }
            start.RegisterSymbolAction(OnSeparateContext, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field);
            start.RegisterSymbolAction(OnUnsafeAccessor, SymbolKind.Method);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnSeparateContextHeld(c)), OperationKind.VariableDeclarator, OperationKind.AnonymousFunction,
                OperationKind.LocalFunction);
            if (role != Role.Tool)
            {
                start.RegisterSymbolAction(OnHeld, SymbolKind.Method, SymbolKind.Property);
                start.RegisterOperationAction(c => Known.Unless(c, () => OnLambda(c)), OperationKind.AnonymousFunction);
            }
            // A Tool runs as the owner before the app exists: its transaction and its connections are its own.
            if (role != Role.Tool)
            {
                start.RegisterOperationAction(c => Known.Unless(c, () => OnSqlCall(c)), OperationKind.Invocation, OperationKind.ObjectCreation);
                start.RegisterOperationAction(c => Known.Unless(c, () => OnSqlText(c)), OperationKind.SimpleAssignment);
                start.RegisterOperationAction(c => Known.Unless(c, () => OnScopeSetting(c)), OperationKind.Literal, OperationKind.Binary,
                    OperationKind.InterpolatedString, OperationKind.FieldReference, OperationKind.LocalReference);
            }
        });
    }

    private static void OnProperty(OperationAnalysisContext context, Role role)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        var owner = property.ContainingType?.ToDisplayString();
        var clock = (owner, property.Name) switch
        {
            ("System.DateTime", "Now" or "UtcNow" or "Today") => true,
            ("System.DateTimeOffset", "Now" or "UtcNow") => true,
            // NodaTime's TimeProvider.System: registered as the IClock where the app composes, taken through the constructor elsewhere.
            ("NodaTime.SystemClock", "Instance") => !Roles.Composes(role),
            ("Microsoft.VisualBasic.DateAndTime", "Now" or "Today" or "TimeOfDay" or "Timer" or "DateString" or "TimeString") => true,
            ("System.TimeProvider", "System") => !Roles.Composes(role),
            _ => false,
        };
        if (clock)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.WallClock, context.Operation.Syntax.GetLocation(), $"{property.ContainingType!.Name}.{property.Name}"));
        }
        else if (ReflectionProperties.Contains(property.Name) && owner is "System.Type" or "System.Reflection.TypeInfo")
        {
            OnReflected(context, role, property.Name, Denoted(((IPropertyReferenceOperation)context.Operation).Instance, 0));
        }
        // An operation's type, read from its descriptor, is what turns a name into an input to send.
        else if (owner == "Levee.OperationDescriptor" && property.Name is "InputType" or "ResultType")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ByName, context.Operation.Syntax.GetLocation(), $"{property.ContainingType!.Name}.{property.Name} is read"));
        }
    }

    // Statements that open, end or shape a transaction, at a statement's start once literals, comments and
    // dollar-quoted bodies are gone (a DO block's END, a CASE's END aren't).
    private static readonly Regex TransactionSql = new(
        @"(?:^|;)\s*(begin|commit|rollback|end|abort|start\s+transaction|savepoint|release|prepare\s+transaction|set\s+transaction|set\s+session\s+characteristics|set\s+(?:local\s+|session\s+)?role|reset\s+role|set\s+session\s+authorization)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SqlNoise = new(@"\$[A-Za-z_]*\$.*?\$[A-Za-z_]*\$|'(?:[^']|'')*'|--[^\n]*|/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The SQL a constant or interpolated string carries, its holes left out.</summary>
    private static string? Sql(IOperation value) => value switch
    {
        { ConstantValue: { HasValue: true, Value: string text } } => text,
        IInterpolatedStringOperation interpolated => string.Concat(interpolated.Parts.OfType<IInterpolatedStringTextOperation>()
            .Select(p => p.Text.ConstantValue.Value as string)),
        IConversionOperation conversion => Sql(conversion.Operand),
        _ => null,
    };

    private static string? TransactionStatement(string sql) =>
        TransactionSql.Match(SqlNoise.Replace(sql, " ")) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    private static void OnSqlText(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is IPropertyReferenceOperation { Property.Name: "CommandText" } && Sql(assignment.Value) is { } sql
            && TransactionStatement(sql) is { } statement)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, assignment.Syntax.GetLocation(), $"SQL '{statement}'"));
        }
    }

    /// <summary>SQL handed to EF (<c>ExecuteSqlRaw</c>, <c>FromSql</c>), a command or Dapper, by its parameter's name.</summary>
    private static void OnSqlCall(OperationAnalysisContext context)
    {
        var (name, arguments) = context.Operation switch
        {
            IInvocationOperation i => (i.TargetMethod.Name, i.Arguments),
            IObjectCreationOperation o => (o.Constructor?.ContainingType.Name ?? "", o.Arguments),
            _ => ("", ImmutableArray<IArgumentOperation>.Empty),
        };
        foreach (var argument in arguments.Where(a => a.Parameter?.Name is "sql" or "commandText" or "cmdText" or "command"))
        {
            if (Sql(argument.Value) is { } sql && TransactionStatement(sql) is { } statement)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, argument.Syntax.GetLocation(), $"{name} running '{statement}'"));
                return;
            }
        }
    }

    /// <summary>
    /// SQL that sets the settings row-level security trusts (<c>levee.scope</c>, <c>levee.platform</c>), or writes the audit or
    /// an idempotency key by hand: however the string is put together (a literal, a concatenation, an interpolation, SQL's own
    /// concatenation), it would move the operation into another tenant's rows, or forge its record. Reading them is fine, and so
    /// is text that names them without being SQL (a message). A setting's bare name is judged where it's used: by the SQL it's
    /// passed with.
    /// </summary>
    private static void OnScopeSetting(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        // The whole string once, at its outermost piece.
        if (operation.Parent is IInterpolatedStringTextOperation or IInterpolationOperation or IInterpolatedStringOperation
            || operation.Parent is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String }
            || operation.Parent?.ConstantValue is { HasValue: true, Value: string }
            || Flatten(operation) is not { } text)
        {
            return;
        }
        // A migration's SQL, handed to EF's builder, runs as the owner at migrate, never inside an operation: it may backfill
        // the audit. The rest of a migration class (a helper, a constant, a string it keeps) is held like any other code.
        if (InMigration(context.ContainingSymbol) && ToMigrationBuilder(operation))
        {
            return;
        }
        var flat = Flat(text);
        var names = flat.Contains("levee.scope") || flat.Contains("levee.plat") || flat.Contains("leveescope") || flat.Contains("leveeplatform")
            || flat.Contains("levee." + Hole);
        var refused = (names && Sets(text)) || CallsLevee(text)
            || PgSettingsWrite.IsMatch(SqlComment.Replace(text, " "))
            || (flat is "levee.scope" or "levee.platform" && !ReadOnly(operation));
        if (refused)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ScopeSetting, operation.Syntax.GetLocation()));
        }
        // Levee's channel delivers by the scope its payload names: a NOTIFY on it from the app's SQL names any scope it likes.
        else if (flat.Contains("levee_signals"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SignalByHand, operation.Syntax.GetLocation()));
        }
    }

    private static bool InMigration(ISymbol? symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "Microsoft.EntityFrameworkCore.Migrations.Migration")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The string is an argument, as written, to one of EF's <c>MigrationBuilder</c> methods (<c>Sql</c>, a column's <c>defaultValueSql</c>).</summary>
    private static bool ToMigrationBuilder(IOperation text)
    {
        var at = text.Parent;
        while (at is IConversionOperation)
        {
            at = at.Parent;
        }
        return at is IArgumentOperation { Parent: IInvocationOperation call }
            && Derives(call.TargetMethod.ContainingType, "Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder");
    }

    private const char Hole = '\u0001';

    /// <summary>A string's text, each piece that isn't a constant a <see cref="Hole"/>.</summary>
    private static string? Flatten(IOperation value) => value switch
    {
        { ConstantValue: { HasValue: true, Value: string text } } => text,
        IInterpolatedStringOperation interpolated => string.Concat(interpolated.Parts.Select(p => p switch
        {
            IInterpolatedStringTextOperation t => t.Text.ConstantValue.Value as string,
            IInterpolationOperation i => Flatten(i.Expression) ?? Hole.ToString(),
            _ => Hole.ToString(),
        })),
        IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } b =>
            (Flatten(b.LeftOperand) ?? Hole.ToString()) + (Flatten(b.RightOperand) ?? Hole.ToString()),
        IConversionOperation conversion => Flatten(conversion.Operand),
        _ => null,
    };

    private static readonly Regex SqlComment = new(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string Flat(string text) =>
        new(SqlComment.Replace(text, " ").ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '"' or ',' or '|' or '(' or ')' or '+')).ToArray());

    private static bool Sets(string text)
    {
        var clean = SqlComment.Replace(text, " ");
        return Flat(clean).Contains("set_config") || SettingStatement.IsMatch(clean.Replace("\"", "").Replace("'", ""));
    }

    /// <summary>
    /// A statement that calls one of the functions the runtime role writes the audit or a key through, however its name is
    /// put together: what can do harm is the call, since the role holds no privilege on the tables themselves. SQL is text
    /// that begins as a statement does (a query, a CTE, a DO block), wherever it's written (a helper's, a constant); a
    /// message that names one, as a call or not, doesn't begin that way.
    /// </summary>
    private static bool CallsLevee(string text)
    {
        var clean = SqlComment.Replace(text, " ");
        return Statement.IsMatch(clean)
            && LeveeCall.IsMatch(new string(clean.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '"' or '|' or '+')).ToArray()));
    }

    private static readonly Regex LeveeCall = new(@"levee_(?:audit_write|idempotency_[a-z_]+)\(", RegexOptions.CultureInvariant);

    // How a statement that calls a function begins: a query, a CTE, or a DO block (whose body PERFORMs it).
    private static readonly Regex Statement = new(@"^\s*(?:(?:select|with)\b|do\s*[$'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // pg_settings is the view set_config writes through.
    private static readonly Regex PgSettingsWrite = new(@"\bupdate\s+(?:pg_catalog\s*\.\s*)?pg_settings\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A bare setting name is allowed only where it's read: passed with SQL that reads it (<c>current_setting</c>) and sets
    /// nothing, or held in a <c>const</c> (judged at each use) or a local every use of which reads it.
    /// </summary>
    private static bool ReadOnly(IOperation name)
    {
        if (name.Parent is IFieldInitializerOperation { InitializedFields: var fields } && fields.All(f => f.IsConst))
        {
            return true;
        }
        if (name.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator })
        {
            if (declarator.Symbol.IsConst)
            {
                return true;
            }
            var root = name;
            while (root.Parent is not null)
            {
                root = root.Parent;
            }
            var uses = root.Descendants().OfType<ILocalReferenceOperation>().Where(r => SymbolEqualityComparer.Default.Equals(r.Local, declarator.Symbol)).ToList();
            return uses.Count > 0 && uses.All(ReadAtUse);
        }
        return ReadAtUse(name);
    }

    /// <summary>The name, or the string it's put in, is passed with SQL that reads a setting and sets none.</summary>
    private static bool ReadAtUse(IOperation name)
    {
        IOperation outer = name;
        while (outer.Parent is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } or IInterpolationOperation or IInterpolatedStringOperation or IConversionOperation)
        {
            outer = outer.Parent;
        }
        if (outer != name && Flatten(outer) is { } around && Reads(around))
        {
            return true;
        }
        for (var at = outer.Parent; at is not null and not IBlockOperation and not ISymbolInitializerOperation and not IVariableDeclarationGroupOperation; at = at.Parent)
        {
            var arguments = at switch
            {
                IInvocationOperation i => i.Arguments,
                IObjectCreationOperation o => o.Arguments,
                _ => default,
            };
            if (!arguments.IsDefault)
            {
                var sql = arguments.Where(a => a.Value != outer).Select(a => Flatten(a.Value)).OfType<string>().ToList();
                return sql.Any(Reads) && !sql.Any(Sets);
            }
        }
        return false;
    }

    private static bool Reads(string sql) => Flat(sql).Contains("current_setting") && !Sets(sql);

    // SET [LOCAL|SESSION] levee.…, or RESET levee.…, once quotes are gone ("levee"."scope").
    private static readonly Regex SettingStatement = new(@"(?:^|;)\s*(?:re)?set\s+(?:local\s+|session\s+)?levee\s*\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A connection of its own (outside the operation's transaction), or an ambient one (<c>TransactionScope</c>).</summary>
    private static void OnCreation(OperationAnalysisContext context, Role role)
    {
        var type = ((IObjectCreationOperation)context.Operation).Type as INamedTypeSymbol;
        if (role != Role.Tool && Derives(type, "System.Transactions.TransactionScope"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, context.Operation.Syntax.GetLocation(), "TransactionScope"));
        }
        else if (role != Role.Tool && Derives(type, "System.Data.Common.DbConnection"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SideConnection, context.Operation.Syntax.GetLocation(), $"new {type!.Name}"));
        }
        // A context of its own is a connection of its own; EF's design-time factory (dotnet ef) is the one exception.
        else if (role != Role.Tool && Derives(type, "Microsoft.EntityFrameworkCore.DbContext") && !DesignTime(context.ContainingSymbol.ContainingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SideConnection, context.Operation.Syntax.GetLocation(), $"new {type!.Name}"));
        }
        else if (type is not null && type.ContainingNamespace?.ToDisplayString() == "Levee" && type.Name is "OperationCatalog" or "OperationCatalogPart")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatalogByHand, context.Operation.Syntax.GetLocation(), $"new {type.Name}"));
        }
    }

    /// <summary>
    /// A connection or data source taken from the container (Npgsql's AddNpgsqlDataSource registers both) is a connection of
    /// its own just the same: its commands run outside the operation's transaction. Judged where the container hands it over
    /// (a constructor, an endpoint's or action's parameter, an injected property), so holding the operation's own connection,
    /// from IAmbientTransaction, in a field or a record is fine.
    /// </summary>
    private static void OnHeld(SymbolAnalysisContext context)
    {
        var held = context.Symbol switch
        {
            // A record is data its maker fills (the ambient connection and transaction), not a service the container builds.
            // Only a public constructor is the container's to call; any other is the type's own factory's.
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor when !constructor.ContainingType.IsRecord
                && constructor.DeclaredAccessibility == Accessibility.Public => constructor.Parameters.Cast<ISymbol>(),
            IMethodSymbol { DeclaredAccessibility: Accessibility.Public, MethodKind: MethodKind.Ordinary } action
                when Derives(action.ContainingType, "Microsoft.AspNetCore.Mvc.ControllerBase") => action.Parameters.Cast<ISymbol>(),
            IMethodSymbol method => method.Parameters.Where(Injected).Cast<ISymbol>(),
            IPropertySymbol property when Injected(property) => [property],
            _ => [],
        };
        foreach (var symbol in held)
        {
            Report(context.ReportDiagnostic, symbol, symbol is IParameterSymbol p ? p.Type : ((IPropertySymbol)symbol).Type);
        }
    }

    /// <summary>
    /// A minimal API's handler (a lambda or a method group passed to a Map* call) takes its services as parameters, handed
    /// over by the container. Other delegates (a helper over the operation's own connection, a data source's configuration)
    /// are the app's to call.
    /// </summary>
    private static void OnLambda(OperationAnalysisContext context)
    {
        if (IsEndpointHandler(context.Operation))
        {
            foreach (var parameter in ((IAnonymousFunctionOperation)context.Operation).Symbol.Parameters)
            {
                Report(context.ReportDiagnostic, parameter, parameter.Type);
            }
        }
    }

    private static bool IsEndpointHandler(IOperation handler)
    {
        // Through the delegate it becomes, and the conversion to Delegate a Map* overload takes it as.
        var at = handler.Parent;
        while (at is IDelegateCreationOperation or IConversionOperation)
        {
            at = at.Parent;
        }
        return at is IArgumentOperation { Parent: IInvocationOperation map } && map.TargetMethod.Name.StartsWith("Map", StringComparison.Ordinal)
            && (map.TargetMethod.ReducedFrom ?? map.TargetMethod) is { IsExtensionMethod: true } extension && extension.Parameters.Length > 0
            && extension.Parameters[0].Type is var receiver
            && (receiver.ToDisplayString() == "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"
                || receiver.AllInterfaces.Any(i => i.ToDisplayString() == "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"));
    }

    /// <summary>
    /// A separate bounded context's DbContext held by the app's own code, in any role: only the library it serves uses it. Where
    /// the app composes, it's named as a registration's type argument (<c>AddDbContext&lt;T&gt;</c>, <c>UseDbContext&lt;T&gt;</c>,
    /// <c>typeof(T)</c>), never held.
    /// </summary>
    private static void OnSeparateContext(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol.ContainingType is not { } owner || Separate(owner) is not null || DesignTime(owner)
            || symbol.Locations.FirstOrDefault() is not { IsInSource: true } location || Known.IsLeveeGenerated(location.SourceTree))
        {
            return;
        }
        var types = symbol switch
        {
            IMethodSymbol m => m.Parameters.Select(p => p.Type).Append(m.ReturnType),
            IPropertySymbol p => [p.Type],
            IFieldSymbol f => [f.Type],
            _ => [],
        };
        if (types.Select(Separate).FirstOrDefault(t => t is not null) is { } separate)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SeparateContextReach, location, $"{owner.Name}.{symbol.Name}", separate.Name));
        }
    }

    /// <summary>A local, or a lambda's or local function's parameter or return, holding a separate bounded context's DbContext.</summary>
    private static void OnSeparateContextHeld(OperationAnalysisContext context)
    {
        var (name, types) = context.Operation switch
        {
            IVariableDeclaratorOperation local => (local.Symbol.Name, new[] { local.Symbol.Type }),
            IAnonymousFunctionOperation lambda => ("a lambda", lambda.Symbol.Parameters.Select(p => p.Type).Append(lambda.Symbol.ReturnType).ToArray()),
            ILocalFunctionOperation function => (function.Symbol.Name, function.Symbol.Parameters.Select(p => p.Type).Append(function.Symbol.ReturnType).ToArray()),
            _ => ("", []),
        };
        if (context.ContainingSymbol.ContainingType is { } owner && Separate(owner) is null && !DesignTime(owner) && types.Select(Separate).FirstOrDefault(t => t is not null) is { } separate)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SeparateContextReach, context.Operation.Syntax.GetLocation(), $"{owner.Name}: {name}", separate.Name));
        }
    }

    /// <summary>A DbContext marked as a separate bounded context's, or one wrapped (Task&lt;T&gt;, Lazy&lt;T&gt;, IDbContextFactory&lt;T&gt;).</summary>
    private static INamedTypeSymbol? Separate(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol named when Derives(named, "Microsoft.EntityFrameworkCore.DbContext")
            && named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Levee.EntityFrameworkCore.SeparateContextAttribute") => named,
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Select(Separate).FirstOrDefault(c => c is not null),
        IArrayTypeSymbol array => Separate(array.ElementType),
        _ => null,
    };

    /// <summary>EF's design-time factory: dotnet ef's to call, by reflection, to make a context for its tooling.</summary>
    private static bool DesignTime(INamedTypeSymbol? type) => type?.AllInterfaces.Any(i => i.Name == "IDesignTimeDbContextFactory") == true;

    private static bool Injected(ISymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name is
        "FromServicesAttribute" or "FromKeyedServicesAttribute" or "InjectAttribute");

    private static void Report(Action<Diagnostic> report, ISymbol symbol, ITypeSymbol type)
    {
        if (Connection(type) is { } connection && symbol.Locations.FirstOrDefault() is { IsInSource: true } location
            && !Known.IsLeveeGenerated(location.SourceTree))
        {
            report(Diagnostic.Create(Descriptors.SideConnection, location, $"{symbol.Name}, a {connection.Name} the container hands it,"));
        }
    }

    /// <summary>A connection or data source type, or one wrapped (Lazy&lt;T&gt;, IEnumerable&lt;T&gt;, Func&lt;T&gt;).</summary>
    private static INamedTypeSymbol? Connection(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol named when Derives(named, "System.Data.Common.DbConnection") || Derives(named, "System.Data.Common.DbDataSource") => named,
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Select(Connection).FirstOrDefault(c => c is not null),
        IArrayTypeSymbol array => Connection(array.ElementType),
        _ => null,
    };

    private static void OnMethodGroup(OperationAnalysisContext context, Role role)
    {
        var reference = (IMethodReferenceOperation)context.Operation;
        if (role != Role.Tool && IsEndpointHandler(reference))
        {
            foreach (var parameter in reference.Method.Parameters)
            {
                Report(context.ReportDiagnostic, parameter, parameter.Type);
            }
        }
        OnCall(context, role, reference.Method);
    }

    private static bool IsLevee(INamedTypeSymbol? type, string name) =>
        type is not null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == "Levee";

    private static bool Derives(INamedTypeSymbol? type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString() == name)
            {
                return true;
            }
        }
        return false;
    }

    private static readonly ImmutableHashSet<string> DataSourceMembers = ImmutableHashSet.Create(
        "CreateConnection", "OpenConnection", "OpenConnectionAsync", "CreateCommand", "CreateBatch");

    // Type's lookups (and System.Reflection.TypeExtensions' of the same names), RuntimeReflectionExtensions', TypeInfo's, and
    // what makes an instance from a Type.
    private static readonly ImmutableHashSet<string> ReflectionMembers = ImmutableHashSet.Create(
        "GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields", "GetMember", "GetMembers", "GetEvent", "GetEvents",
        "GetConstructor", "GetConstructors", "GetNestedType", "GetNestedTypes", "GetInterface", "GetInterfaces", "GetDefaultMembers", "FindMembers",
        "InvokeMember", "GetRuntimeMethod", "GetRuntimeMethods", "GetRuntimeProperty", "GetRuntimeProperties", "GetRuntimeField", "GetRuntimeFields",
        "GetRuntimeEvent", "GetRuntimeEvents", "GetDeclaredMethod", "GetDeclaredMethods", "GetDeclaredProperty", "GetDeclaredField", "GetDeclaredEvent",
        "GetDeclaredNestedType", "CreateInstance", "GetUninitializedObject");

    private static readonly ImmutableHashSet<string> ReflectionProperties = ImmutableHashSet.Create(
        "DeclaredMembers", "DeclaredMethods", "DeclaredProperties", "DeclaredFields", "DeclaredConstructors", "DeclaredEvents", "DeclaredNestedTypes",
        "TypeInitializer");

    /// <summary>An operation of a type known only at run time, handed to the Dispatcher through dynamic.</summary>
    private static void OnByName(OperationAnalysisContext context)
    {
        if (context.Operation is IDynamicInvocationOperation { Operation: IDynamicMemberReferenceOperation { Instance.Type: INamedTypeSymbol receiver } member }
            && IsLevee(receiver, "Dispatcher"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ByName, context.Operation.Syntax.GetLocation(), $"Dispatcher.{member.MemberName} is called through dynamic"));
        }
    }

    /// <summary>
    /// OperationAttribute is only ever written on a contract. Named anywhere else (a type argument, as in
    /// OfType&lt;OperationAttribute&gt;(); a pattern, a cast, typeof, an alias), it's the key of the catalog's names read back
    /// by reflection, whatever reflection API reads them.
    /// </summary>
    private static void OnOperationAttributeNamed(SyntaxNodeAnalysisContext context)
    {
        var name = (IdentifierNameSyntax)context.Node;
        if (name.Identifier.ValueText != "OperationAttribute" || Known.IsLeveeGenerated(name.SyntaxTree)
            || name.Ancestors().Any(a => a is AttributeSyntax or CrefSyntax)
            || context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol is not INamedTypeSymbol type || !IsLevee(type, "OperationAttribute"))
        {
            return;
        }
        var named = name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ? qualified : (SyntaxNode)name;
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.ByName, named.GetLocation(), $"{named} is named outside an attribute, to read [Operation] by reflection"));
    }

    /// <summary>A process holding the Dispatcher: whatever it sends steps out of its starter's identity and scope.</summary>
    private static void OnProcess(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.AllInterfaces.Any(i => Known.Is(i, "IProcess", 1)))
        {
            return;
        }
        var held = type.InstanceConstructors.SelectMany(c => c.Parameters).Select(p => ((ISymbol)p, p.Type))
            .Concat(type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsImplicitlyDeclared).Select(f => ((ISymbol)f, f.Type)))
            .Concat(type.GetMembers().OfType<IPropertySymbol>().Select(p => ((ISymbol)p, p.Type)));
        foreach (var (symbol, heldType) in held)
        {
            if (Holds(heldType, "Dispatcher") && symbol.Locations.FirstOrDefault() is { IsInSource: true } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessSends, location, type.Name));
                return;
            }
        }
    }

    /// <summary>
    /// A handler holding a clock reads a time that isn't its operation's: injected, it's TimeProvider.System in production,
    /// the wall clock by another name. An adapter's or a process's clock, taken the same way, is theirs to hold.
    /// </summary>
    private static void OnHandlerClock(SymbolAnalysisContext context, Role role)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var handler = type.AllInterfaces.Any(i => Known.Is(i, "IOperationHandler", 2) || Known.Is(i, "ISubscriptionHandler", 2));
        // In Application a clock is a process's alone: a helper holding one hands the wall clock to whatever handler takes it.
        var helper = !handler && role == Role.Application && type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.AllInterfaces.Any(i => Known.Is(i, "IProcess", 1))
            && Clock(type) is null;
        if (!handler && !helper)
        {
            return;
        }
        var held = type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).SelectMany(c => c.Parameters).Select(p => ((ISymbol)p, p.Type))
            .Concat(type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsImplicitlyDeclared && !f.IsStatic).Select(f => ((ISymbol)f, f.Type)))
            .Concat(type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic).Select(p => ((ISymbol)p, p.Type)));
        foreach (var (symbol, heldType) in held)
        {
            if (Clock(heldType) is { } clock && symbol.Locations.FirstOrDefault() is { IsInSource: true } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(handler ? Descriptors.HandlerClock : Descriptors.HelperClock, location, type.Name, clock.Name));
                return;
            }
        }
    }

    /// <summary>
    /// TimeProvider or NodaTime's IClock (or what implements it), a port or delegate that answers a time at once without
    /// being handed one (a port wrapping TimeProvider is the wall clock by another name, whatever else it's asked: a time
    /// zone, a calendar), or one wrapped (Lazy&lt;T&gt;, an array). A vendor's time is awaited; a time worked out from another
    /// is handed that one.
    /// </summary>
    private static INamedTypeSymbol? Clock(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named when Derives(named.OriginalDefinition, "System.TimeProvider")
            || named.OriginalDefinition.AllInterfaces.Prepend(named.OriginalDefinition).Any(i => i.ToDisplayString() == "NodaTime.IClock") => named,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } named when TellsTime(invoke) => named,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } named when Known.IsPort(named) && named.AllInterfaces.Prepend(named).SelectMany(i => i.GetMembers()).Any(m => m switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary } method => TellsTime(method),
            IPropertySymbol property => IsTime(property.Type) && !property.Parameters.Any(p => IsTime(p.Type)),
            _ => false,
        }) => named,
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Select(Clock).FirstOrDefault(c => c is not null),
        IArrayTypeSymbol array => Clock(array.ElementType),
        _ => null,
    };

    private static bool TellsTime(IMethodSymbol method) => IsTime(method.ReturnType) && !method.Parameters.Any(p => IsTime(p.Type));

    private static bool IsTime(ITypeSymbol type) =>
        (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type).ToDisplayString() is
            "System.DateTime" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "NodaTime.Instant" or "NodaTime.ZonedDateTime"
            or "NodaTime.OffsetDateTime" or "NodaTime.LocalDateTime" or "NodaTime.LocalDate" or "NodaTime.LocalTime";

    /// <summary>Levee's <paramref name="name"/>, or one wrapped (Lazy&lt;T&gt;, Func&lt;T&gt;, an array).</summary>
    private static bool Holds(ITypeSymbol type, string name) => type switch
    {
        INamedTypeSymbol named when IsLevee(named, name) => true,
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Any(t => Holds(t, name)),
        IArrayTypeSymbol array => Holds(array.ElementType, name),
        _ => false,
    };

    /// <summary>A principal made in the app's core or an adapter: who an operation runs as is decided where the app composes.</summary>
    private static void OnPrincipalMade(OperationAnalysisContext context, Role role)
    {
        if (context.Operation.Type is INamedTypeSymbol type && Derives(type, "Levee.Principal"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.PrincipalMade, context.Operation.Syntax.GetLocation(), type.Name, role));
        }
    }

    /// <summary>
    /// A principal held in the core or an adapter, handed over by the container or whoever made it: what holds one sends as
    /// it, not as the caller its context names, and the process or handler that holds the holder escapes its starter's scope.
    /// </summary>
    private static void OnPrincipalHeld(SymbolAnalysisContext context, Role role)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var held = type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).SelectMany(c => c.Parameters).Select(p => ((ISymbol)p, p.Type))
            .Concat(type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsImplicitlyDeclared).Select(f => ((ISymbol)f, f.Type)))
            .Concat(type.GetMembers().OfType<IPropertySymbol>().Select(p => ((ISymbol)p, p.Type)));
        foreach (var (symbol, heldType) in held)
        {
            if (HeldPrincipal(heldType) is { } principal && symbol.Locations.FirstOrDefault() is { IsInSource: true } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.PrincipalHeld, location, $"{type.Name}.{symbol.Name}", principal.Name, role));
                return;
            }
        }
    }

    /// <summary>
    /// A principal type, or one wrapped (Lazy&lt;T&gt;, Func&lt;T&gt;, an array). A delegate holds only what it hands back: one
    /// that takes a principal (Action&lt;Principal&gt;) is given the caller's, and gives none.
    /// </summary>
    private static INamedTypeSymbol? HeldPrincipal(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named when Derives(named.OriginalDefinition, "Levee.Principal") => named,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } => invoke.Parameters
            .Where(p => p.RefKind is RefKind.Out or RefKind.Ref).Select(p => p.Type).Prepend(invoke.ReturnType)
            .Select(HeldPrincipal).FirstOrDefault(p => p is not null),
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Select(HeldPrincipal).FirstOrDefault(p => p is not null),
        IArrayTypeSymbol array => HeldPrincipal(array.ElementType),
        _ => null,
    };

    private static void OnInvocation(OperationAnalysisContext context, Role role)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (ReflectionMembers.Contains(invocation.TargetMethod.Name) && OnReflected(context, role, invocation.TargetMethod.Name, Reflected(invocation)))
        {
            return;
        }
        OnCall(context, role, invocation.TargetMethod);
    }

    // What LEVEE005 guards as service location, by the types that do it.
    private static readonly ImmutableHashSet<string> LocatorTypes = ImmutableHashSet.Create(
        "System.IServiceProvider", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", "Microsoft.Extensions.DependencyInjection.IServiceScope",
        "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider", "Microsoft.Extensions.DependencyInjection.ISupportRequiredService",
        "Microsoft.Extensions.DependencyInjection.ActivatorUtilities", "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions", "Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions");

    /// <summary>
    /// Reflection into one of Levee's types, anywhere (LEVEE020), or into what locates services, outside where the app composes
    /// (LEVEE005): the same lookup by another route. True when it reported.
    /// </summary>
    private static bool OnReflected(OperationAnalysisContext context, Role role, string member, Denotation? into)
    {
        if (into is { Levee: true } levee)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Reflected, context.Operation.Syntax.GetLocation(), member, levee.Name));
            return true;
        }
        if (into is { } locator && LocatorTypes.Contains(locator.Full) && !Roles.Composes(role))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ServiceLocation, context.Operation.Syntax.GetLocation(), $"{member} on {locator.Name}, by reflection,"));
            return true;
        }
        return false;
    }

    /// <summary>Of what a variable may hold, the one that tells most: Levee's, then a locator, then any.</summary>
    private static Denotation? Telling(List<Denotation?> held) =>
        held.FirstOrDefault(d => d?.Levee == true) ?? held.FirstOrDefault(d => d is { } t && LocatorTypes.Contains(t.Full)) ?? held.FirstOrDefault(d => d is not null);

    /// <summary>A type an expression statically names: its full name, its short one, and whether it's one of Levee's.</summary>
    private readonly record struct Denotation(string Full, string Name, bool Levee);

    private static Denotation? Of(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
            ? new Denotation(named.OriginalDefinition.ToDisplayString(), named.Name,
                named.ContainingAssembly?.Name is { } assembly && (assembly == "Levee" || assembly.StartsWith("Levee.", StringComparison.Ordinal)))
            : null;

    /// <summary>A type named by its assembly-qualified or full name, as Type.GetType and [UnsafeAccessorType] take it.</summary>
    private static Denotation? Of(string? name) =>
        name?.Split(',')[0].Trim() is { Length: > 0 } full
            ? new Denotation(full, full.Substring(full.LastIndexOfAny(['.', '+']) + 1), full.StartsWith("Levee.", StringComparison.Ordinal))
            : null;

    /// <summary>The type a reflection lookup is made on, or an instance made from: its receiver, an extension's, or the Type an activator takes.</summary>
    private static Denotation? Reflected(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.ContainingType.ToDisplayString() is "System.Activator" or "System.Runtime.CompilerServices.RuntimeHelpers")
        {
            return method.TypeArguments.Select(Of).FirstOrDefault(t => t is not null)
                ?? Denoted(invocation.Arguments.FirstOrDefault(a => a.Value.Type?.ToDisplayString() == "System.Type")?.Value, 0);
        }
        return Denoted(invocation.Instance ?? (method.IsExtensionMethod ? invocation.Arguments.FirstOrDefault()?.Value : null), 0);
    }

    /// <summary>
    /// The type a Type-valued expression statically names: typeof(T); x.GetType() of one; Type.GetType("..."); its
    /// GetTypeInfo(); or a local, or a field initialized in the same file, that holds one. What it can't follow (a parameter, a
    /// field set elsewhere, a name built at run time) is the trust model's.
    /// </summary>
    private static Denotation? Denoted(IOperation? value, int depth)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }
        switch (value)
        {
            case ITypeOfOperation typeOf:
                return Of(typeOf.TypeOperand);
            case IInvocationOperation { TargetMethod: { Name: "GetType", IsStatic: false }, Instance: { } instance, Arguments.Length: 0 }:
                return Of(instance.Type);
            case IInvocationOperation { TargetMethod: { Name: "GetType", IsStatic: true } named } byName when named.ContainingType.ToDisplayString() == "System.Type":
                return Of(byName.Arguments.FirstOrDefault()?.Value.ConstantValue.Value as string);
            case IInvocationOperation { TargetMethod.Name: "GetTypeInfo" or "AsType" } info:
                return Denoted(info.Instance ?? info.Arguments.FirstOrDefault()?.Value, depth);
            case ILocalReferenceOperation local when depth < 4:
                var root = value;
                while (root.Parent is not null)
                {
                    root = root.Parent;
                }
                return Telling([.. root.Descendants()
                    .Select(o => o switch
                    {
                        IVariableDeclaratorOperation d when SymbolEqualityComparer.Default.Equals(d.Symbol, local.Local) => d.Initializer?.Value,
                        ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } a when SymbolEqualityComparer.Default.Equals(target.Local, local.Local) => a.Value,
                        _ => null,
                    })
                    .Select(v => v is null ? null : Denoted(v, depth + 1))]);
            case IFieldReferenceOperation field when depth < 4 && value.SemanticModel is { } model:
                return Telling([.. field.Field.DeclaringSyntaxReferences
                    .Where(r => r.SyntaxTree == model.SyntaxTree)
                    .Select(r => (r.GetSyntax() as Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax)?.Initializer?.Value)
                    .Select(v => v is null ? null : Denoted(model.GetOperation(v), depth + 1))]);
            default:
                return null;
        }
    }

    /// <summary>An [UnsafeAccessor] onto one of Levee's types: a call into what it keeps internal, with no lookup to see.</summary>
    private static void OnUnsafeAccessor(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnsafeAccessorAttribute")
            is not { } accessor)
        {
            return;
        }
        static Denotation? Named(ImmutableArray<AttributeData> attributes) =>
            Of(attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute")?
                .ConstructorArguments.FirstOrDefault().Value as string);
        // Its target is the type it constructs (UnsafeAccessorKind.Constructor, 0), or else its first parameter's.
        var target = accessor.ConstructorArguments.FirstOrDefault().Value is 0
            ? Named(method.GetReturnTypeAttributes()) ?? Of(method.ReturnType)
            : method.Parameters.FirstOrDefault() is { } first ? Named(first.GetAttributes()) ?? Of(first.Type) : null;
        if (target is { Levee: true } levee && method.Locations.FirstOrDefault() is { IsInSource: true } location)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Reflected, location, $"[UnsafeAccessor] {method.Name}", levee.Name));
        }
    }

    private static void OnCall(OperationAnalysisContext context, Role role, IMethodSymbol method)
    {
        // What clients see, and what's pure, sends nothing: a contract that dispatches itself is a surface of its own.
        if (role is Role.Kernel or Role.Contracts or Role.Domain && IsLevee(method.ContainingType, "Dispatcher"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ByName, context.Operation.Syntax.GetLocation(), $"{role} calls Dispatcher.{method.Name}"));
            return;
        }
        // Public only because the root's generated composition, in another assembly, calls it.
        var generatedRegistration = method.ContainingType.Name.StartsWith("LeveeRegistration_", StringComparison.Ordinal)
            && method.ContainingType.ContainingNamespace?.ToDisplayString() == "Levee.Generated";
        if ((IsLevee(method.ContainingType, "OperationDescriptor") && method.Name is "Create" or "CreateSubscription")
            || (IsLevee(method.ContainingType, "LeveeServiceCollectionExtensions") && method.Name == "AddLeveePart")
            || generatedRegistration
            || (IsLevee(method.ContainingType, "PortSelection") && method.Name is "Select" or "Wants" or "Bind"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatalogByHand, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
            return;
        }
        // The run-time guard's one way around, for a Tool's owner work.
        if (role != Role.Tool && method.Name == "Run" && method.ContainingType.ToDisplayString() == "Levee.EntityFrameworkCore.LeveeOwner")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.OwnerRun, context.Operation.Syntax.GetLocation()));
            return;
        }
        // Resolving it is holding it, wherever the app composes too.
        if (LocatorMembers.Contains(method.Name) && IsLocator(method)
            && (method.TypeArguments.Select(Separate).FirstOrDefault(c => c is not null)
                ?? (context.Operation as IInvocationOperation)?.Arguments.Select(a => (a.Value as ITypeOfOperation)?.TypeOperand).Select(Separate).FirstOrDefault(c => c is not null)) is { } resolved)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SeparateContextReach, context.Operation.Syntax.GetLocation(), $"{method.Name}<{resolved.Name}>", resolved.Name));
            return;
        }
        var made = method.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        if (role != Role.Tool && ((DataSourceMembers.Contains(method.Name) && Derives(method.ContainingType, "System.Data.Common.DbDataSource"))
            || (method.Name == "CreateConnection" && Derives(method.ContainingType, "System.Data.Common.DbProviderFactory"))
            || (method.Name is "Clone" or "CloneWith" && Derives(method.ContainingType, "System.Data.Common.DbConnection"))
            || (method.Name == "GetDbConnection" && method.ContainingType.ToDisplayString() == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions")
            || (method.Name is "GetService" or "GetInfrastructure" && method.ContainingType.ToDisplayString() == "Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions")
            || (method.Name == "CreateInstance" && method.ContainingType.ToDisplayString() == "System.Activator"
                && (Derives(made, "System.Data.Common.DbConnection") || Derives(made, "Microsoft.EntityFrameworkCore.DbContext")))
            // EF's design-time factory is dotnet ef's to call, by reflection: called from code, it's a context with no guard.
            || (method.Name is "CreateDbContext" or "CreateDbContextAsync"
                && method.ContainingType.AllInterfaces.Prepend(method.ContainingType).Any(i => $"{i.ContainingNamespace}.{i.MetadataName}"
                    is "Microsoft.EntityFrameworkCore.IDbContextFactory`1" or "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1"))))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SideConnection, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
            return;
        }
        // CreateVersion7() with no time of its own reads the wall clock.
        if (method.Name == "CreateVersion7" && method.Parameters.Length == 0 && method.ContainingType.ToDisplayString() == "System.Guid")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.WallClock, context.Operation.Syntax.GetLocation(), "Guid.CreateVersion7()"));
            return;
        }
        if (role != Role.Tool && ((TransactionMembers.Contains(method.Name) && IsPersistence(method.ContainingType))
            || (TransactionObjectMembers.Contains(method.Name) && IsTransaction(method.ContainingType))))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
        }
        else if (role != Role.Tool && LocatorMembers.Contains(method.Name) && IsLocator(method)
            && (method.TypeArguments.Select(Connection).FirstOrDefault(c => c is not null)
                ?? (context.Operation as IInvocationOperation)?.Arguments.Select(a => (a.Value as ITypeOfOperation)?.TypeOperand).Select(Connection).FirstOrDefault(c => c is not null)) is { } located
            // Composition and a Host resolve the data source to configure the context with; a connection is the side door.
            && (!Roles.Composes(role) || Derives(located, "System.Data.Common.DbConnection")))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SideConnection, context.Operation.Syntax.GetLocation(), $"{method.Name}<{located.Name}>"));
        }
        else if (!Roles.Composes(role) && LocatorMembers.Contains(method.Name) && IsLocator(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ServiceLocation, context.Operation.Syntax.GetLocation(), method.Name));
        }
    }

    private static bool IsPersistence(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.ToDisplayString();
            if (name is "Microsoft.EntityFrameworkCore.DbContext" or "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
                or "System.Data.Common.DbConnection")
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTransaction(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }
        static bool Named(INamedTypeSymbol t) => t.ToDisplayString() is "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"
            or "System.Data.Common.DbTransaction" or "System.Data.IDbTransaction";
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (Named(t))
            {
                return true;
            }
        }
        return type.AllInterfaces.Any(Named);
    }

    private static bool IsLocator(IMethodSymbol method)
    {
        var owner = method.ContainingType.ToDisplayString();
        // EF's accessor reaches the context's internal services (its connection among them).
        if (owner is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider" or "Microsoft.Extensions.DependencyInjection.ActivatorUtilities"
            or "Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions")
        {
            return true;
        }
        var receiver = method.IsExtensionMethod ? (method.ReducedFrom ?? method).Parameters.FirstOrDefault()?.Type.ToDisplayString() : null;
        return receiver is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider";
    }
}
