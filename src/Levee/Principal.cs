namespace Levee;

/// <summary>
/// Who an operation runs as. Established by the host (a key, a session, a job), never by the caller's input. Apps derive
/// their own principals; their <see cref="IAccessPolicy"/> knows them.
/// </summary>
public abstract record Principal(string Subject);

/// <summary>
/// A caller with no credentials, for what anyone may do (a customer's status page, a sign-in). The access policy decides
/// what that is; an operation that runs in its caller's own scope answers it <see cref="ErrorKind.Unauthenticated"/>, since
/// what it lacks is credentials, not permission.
/// </summary>
public sealed record AnonymousPrincipal() : Principal("anonymous");

/// <summary>The platform acting on its own behalf: bootstrapping, periodic work. The access policy still decides what it may do.</summary>
public sealed record SystemPrincipal(string Subject) : Principal(Subject);

/// <summary>
/// A job, running for whoever enqueued it: <see cref="Principal.Subject"/> is theirs, so the audit names them, and
/// <see cref="System"/> says whether that was the platform itself. The enqueue was authorized as them; the access policy
/// decides again what the job may do now.
/// </summary>
/// <param name="Subject">Whoever enqueued it.</param>
/// <param name="System">Whether that was the platform itself.</param>
/// <param name="Scope">The scope of the principal that enqueued it, when it carried one: a caller-scoped operation runs there.</param>
public sealed record JobPrincipal(string Subject, bool System, string? Scope = null) : Principal(Subject), IScopedPrincipal
{
    string IScopedPrincipal.ScopeKey => Scope ?? "";
}

/// <summary>
/// A principal bound to one scope by its credentials (an API key issued to a tenant): an operation declared
/// <see cref="OperationAttribute.CallerScoped"/> runs in it, so the input needn't, and can't, name another.
/// </summary>
public interface IScopedPrincipal
{
    string ScopeKey { get; }
}
