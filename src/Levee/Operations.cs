namespace Levee;

public interface IOperation;

/// <summary>A capability of the platform. Declared with <see cref="OperationAttribute"/>; handled by exactly one handler.</summary>
public interface IOperation<TResult> : IOperation;

/// <summary>
/// A stream of <typeparamref name="TItem"/>: what changed, as it happens. Declared like an operation and authorized and
/// scoped once, as it opens; it only reads, holds no transaction, and is projected as server-sent events.
/// </summary>
public interface ISubscription<TItem> : IOperation;

/// <summary>
/// An operation that acts inside one scope (a tenant, a household: whatever the app isolates by). Its unit of work sees
/// only that scope's rows. Every operation is scoped or declares <see cref="OperationAttribute.Platform"/> (LEVEE106).
/// </summary>
public interface IScopedOperation
{
    /// <summary>Never null or blank: the dispatcher refuses an operation without its scope.</summary>
    string ScopeKey { get; }
}

[Flags]
public enum Effects
{
    None = 0,
    Read = 1,
    Write = 2,

    /// <summary>Acts on the world outside the platform's database (a message, a payment, a vendor call).</summary>
    External = 4,
}

public enum Idempotency
{
    /// <summary>No effects: retry freely.</summary>
    Safe,

    /// <summary>Repeating it leaves the same state.</summary>
    Idempotent,

    /// <summary>Repeating it is refused as a conflict (a natural key already taken).</summary>
    Conflicts,

    None,
}

/// <param name="name">Stable, dotted and lowercase (<c>notes.create</c>): the name every projection exposes and jobs store.</param>
/// <param name="access">What the caller needs, in the app's own vocabulary; the app's <see cref="IAccessPolicy"/> decides.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OperationAttribute(string name, string access) : Attribute
{
    public string Name { get; } = name;
    public string Access { get; } = access;
    public string Summary { get; set; } = "";

    /// <summary>Only reads, unless declared: an undeclared write fails at the database.</summary>
    public Effects Effects { get; set; } = Effects.Read;

    public Idempotency Idempotency { get; set; } = Idempotency.Safe;

    /// <summary>Acts across every scope: its unit of work sees every row. Declared, never the default (LEVEE106).</summary>
    public bool Platform { get; set; }

    /// <summary>
    /// Runs in its caller's scope: the principal's (<see cref="IScopedPrincipal"/>), not one its input names. For a caller whose
    /// credentials carry the tenant. One of this, <see cref="IScopedOperation"/> or <see cref="Platform"/> (LEVEE106).
    /// </summary>
    public bool CallerScoped { get; set; }

    /// <summary>
    /// The platform's own: in the catalog and through the pipeline (authorized, scoped, audited) like any other, but never
    /// projected to a client (HTTP, MCP, CLI). For what runs before there's a caller (sign-in) or for the platform alone.
    /// </summary>
    public bool Internal { get; set; }

    /// <summary>
    /// The codes its handler fails with, lowercase and dotted (<c>notes.too_long</c>): its contract, shown to every client.
    /// Levee's own (<see cref="LeveeErrors"/>) needn't be. A literal code the handler doesn't declare fails the build (LEVEE110).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819", Justification = "Attribute arguments are arrays.")]
    public string[] Errors { get; set; } = [];
}

/// <summary>
/// Marks an app's own attribute as operation metadata: put on an operation, it's captured into the catalog
/// (<see cref="OperationDescriptor.Get{TMetadata}"/>), where its rules and every projection read it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OperationMetadataAttribute : Attribute;

/// <summary>
/// Write-only input: never echoed, logged, audited or put in a job. Property-only, so on a positional record it must be
/// <c>[property: Secret]</c>: a bare <c>[Secret]</c> there would land on the constructor parameter and protect nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>
/// Personal data (a name, an address, an email): the handler and the result see it, but the audit and
/// <see cref="IOperationOutcome"/> keep <c>[personal]</c> in its place, since an append-only record can never forget it.
/// Property-only, like <see cref="SecretAttribute"/>: <c>[property: Personal]</c> on a positional record.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalAttribute : Attribute;
