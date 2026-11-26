using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Operations;

namespace Levee.Analyzers;

/// <summary>
/// The role-aware code rules. LEVEE003: no wall clock outside tests (TimeProvider.System only in a Host). LEVEE004: nothing
/// saves or controls a transaction outside tests: the dispatcher commits. LEVEE005: services are located only in a Host or a test.
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
        ImmutableArray.Create(Descriptors.WallClock, Descriptors.Transaction, Descriptors.ServiceLocation, Descriptors.SideConnection);

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
            start.RegisterOperationAction(c => Known.Unless(c, () => OnCreation(c, role)), OperationKind.ObjectCreation);
            // A Tool runs as the owner before the app exists: its transaction and its connections are its own.
            if (role != Role.Tool)
            {
                start.RegisterOperationAction(c => Known.Unless(c, () => OnSqlCall(c)), OperationKind.Invocation, OperationKind.ObjectCreation);
                start.RegisterOperationAction(c => Known.Unless(c, () => OnSqlText(c)), OperationKind.SimpleAssignment);
                start.RegisterOperationAction(c => Known.Unless(c, () => OnScopeSetting(c)), OperationKind.Literal, OperationKind.InterpolatedStringText);
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
        @"(?:^|;)\s*(begin|commit|rollback|end|abort|start\s+transaction|savepoint|release|prepare\s+transaction|set\s+transaction|set\s+session\s+characteristics)\b",
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
    /// The settings row-level security trusts (<c>levee.scope</c>, <c>levee.platform</c>) named anywhere in a string: set
    /// in SQL, built up in a variable, they'd move the operation into another tenant's rows. Only the unit of work sets them.
    /// </summary>
    private static void OnScopeSetting(OperationAnalysisContext context)
    {
        var text = context.Operation switch
        {
            ILiteralOperation { ConstantValue: { HasValue: true, Value: string literal } } => literal,
            IInterpolatedStringTextOperation part => part.Text.ConstantValue.Value as string,
            _ => null,
        };
        if (text is not null && (text.IndexOf("levee.scope", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("levee.platform", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Transaction, context.Operation.Syntax.GetLocation(), "SQL naming Levee's scope setting"));
        }
    }

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
    }

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

    private static void OnInvocation(OperationAnalysisContext context, Role role)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (role != Role.Tool && ((DataSourceMembers.Contains(method.Name) && Derives(method.ContainingType, "System.Data.Common.DbDataSource"))
            || (method.Name is "CreateDbContext" or "CreateDbContextAsync"
                && method.ContainingType.AllInterfaces.Prepend(method.ContainingType).Any(i => i.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.IDbContextFactory<TContext>"))))
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
        if (owner is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider" or "Microsoft.Extensions.DependencyInjection.ActivatorUtilities")
        {
            return true;
        }
        var receiver = method.IsExtensionMethod ? (method.ReducedFrom ?? method).Parameters.FirstOrDefault()?.Type.ToDisplayString() : null;
        return receiver is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider";
    }
}
