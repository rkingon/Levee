using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// The role-aware code rules. LEVEE003: no wall clock outside tests (TimeProvider.System only where the app composes). LEVEE004:
/// nothing saves, controls a transaction, or sets the scope outside tests and Tools: the dispatcher commits. LEVEE005: services
/// are located only where the app composes. LEVEE018: no connection of its own. LEVEE020: the catalog is the generator's.
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
        ImmutableArray.Create(Descriptors.WallClock, Descriptors.Transaction, Descriptors.ScopeSetting, Descriptors.ServiceLocation, Descriptors.SideConnection,
            Descriptors.OwnerRun, Descriptors.CatalogByHand);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code is held to the rules too: a header is no way out. Levee's own output is skipped by its path.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var role = Roles.Of(start.Options);
            if (role is Role.None or Role.Test || !Roles.UsesLevee(start.Compilation))
            {
                return;
            }
            start.RegisterOperationAction(c => Known.Unless(c, () => OnProperty(c, role)), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnInvocation(c, role)), OperationKind.Invocation);
            // A method group is a call deferred: Func<Type, object?> f = services.GetService locates all the same.
            start.RegisterOperationAction(c => Known.Unless(c, () => OnMethodGroup(c, role)), OperationKind.MethodReference);
            start.RegisterOperationAction(c => Known.Unless(c, () => OnCreation(c, role)), OperationKind.ObjectCreation);
            if (role != Role.Tool)
            {
                start.RegisterSymbolAction(OnHeld, SymbolKind.Method, SymbolKind.Field, SymbolKind.Property);
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
            ("System.TimeProvider", "System") => !Roles.Composes(role),
            _ => false,
        };
        if (clock)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.WallClock, context.Operation.Syntax.GetLocation(), $"{property.ContainingType!.Name}.{property.Name}"));
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
    /// SQL that sets the settings row-level security trusts (<c>levee.scope</c>, <c>levee.platform</c>), or writes the audit
    /// by hand: however the string is put together (a literal, a concatenation, an interpolation, SQL's own concatenation),
    /// it would move the operation into another tenant's rows, or forge its record. Reading them is fine. A setting's bare
    /// name is judged where it's used: by the SQL it's passed with.
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
        var flat = Flat(text);
        var names = flat.Contains("levee.scope") || flat.Contains("levee.plat") || flat.Contains("leveescope") || flat.Contains("leveeplatform")
            || flat.Contains("levee." + Hole);
        var refused = (names && Sets(text)) || flat.Contains("levee_audit_write") || PgSettingsWrite.IsMatch(SqlComment.Replace(text, " "))
            || (flat is "levee.scope" or "levee.platform" && !ReadOnly(operation));
        if (refused)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ScopeSetting, operation.Syntax.GetLocation()));
        }
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
        else if (role != Role.Tool && Derives(type, "Microsoft.EntityFrameworkCore.DbContext")
            && context.ContainingSymbol.ContainingType?.AllInterfaces.Any(i => i.Name == "IDesignTimeDbContextFactory") != true)
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
    /// its own just the same: its commands run outside the operation's transaction.
    /// </summary>
    private static void OnHeld(SymbolAnalysisContext context)
    {
        var held = context.Symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.Parameters.Select(p => ((ISymbol)p, p.Type)),
            IFieldSymbol { IsImplicitlyDeclared: false } field => [(field, field.Type)],
            IPropertySymbol { IsImplicitlyDeclared: false } property => [(property, property.Type)],
            _ => [],
        };
        foreach (var (symbol, type) in held)
        {
            if ((Derives(type as INamedTypeSymbol, "System.Data.Common.DbConnection") || Derives(type as INamedTypeSymbol, "System.Data.Common.DbDataSource"))
                && symbol.Locations.FirstOrDefault() is { IsInSource: true } location && !Known.IsLeveeGenerated(location.SourceTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.SideConnection, location, $"{symbol.Name}, a {type.Name} it holds,"));
            }
        }
    }

    private static void OnMethodGroup(OperationAnalysisContext context, Role role) =>
        OnCall(context, role, ((IMethodReferenceOperation)context.Operation).Method);

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

    private static void OnInvocation(OperationAnalysisContext context, Role role) =>
        OnCall(context, role, ((IInvocationOperation)context.Operation).TargetMethod);

    private static void OnCall(OperationAnalysisContext context, Role role, IMethodSymbol method)
    {
        if ((IsLevee(method.ContainingType, "OperationDescriptor") && method.Name is "Create" or "CreateSubscription")
            || (IsLevee(method.ContainingType, "LeveeServiceCollectionExtensions") && method.Name == "AddLeveePart"))
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
            || (TransactionObjectMembers.Contains(method.Name) && IsTransaction(method.ContainingType))
            || IsLeveeUnitOfWork(method)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
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

    /// <summary>Opening, auditing or committing Levee's unit of work: the dispatcher's alone, or it runs unauthorized and unaudited.</summary>
    private static bool IsLeveeUnitOfWork(IMethodSymbol method)
    {
        static bool Is(INamedTypeSymbol t, string name) => t.Name == name && t.ContainingNamespace is { Name: "Levee", ContainingNamespace.IsGlobalNamespace: true };
        var type = method.ContainingType;
        return (method.Name == "BeginAsync" && (Is(type, "IUnitOfWorkFactory") || type.AllInterfaces.Any(i => Is(i, "IUnitOfWorkFactory"))))
            || (method.Name is "CommitAsync" or "AuditAsync" && (Is(type, "IUnitOfWork") || type.AllInterfaces.Any(i => Is(i, "IUnitOfWork"))));
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
