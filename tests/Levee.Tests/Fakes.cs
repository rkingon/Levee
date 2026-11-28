using Levee;

namespace Levee.Tests;

public sealed record Member(string Subject, string? Tenant, bool Admin = false) : Principal(Subject);

public sealed record Stranger(string Subject) : Principal(Subject);

/// <summary>An API key issued to one tenant: its credentials carry the scope.</summary>
public sealed record Key(string Subject, string Tenant) : Principal(Subject), IScopedPrincipal
{
    string IScopedPrincipal.ScopeKey => Tenant;
}

/// <summary>Platform operations need an admin; a member acts only in their own tenant.</summary>
[Refuses("access.platform", "access.tenant", "access.unknown")]
internal sealed class TestAccess : IAccessPolicy
{
    public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ValueTask.FromResult(request.Principal switch
    {
        SystemPrincipal or Member { Admin: true } => null,
        Member m when request.Operation.Access == "platform" => Error.Forbidden("access.platform", $"{m.Subject} isn't an admin."),
        Member m when request.Scope.Key is { } key && key != m.Tenant => Error.Forbidden("access.tenant", "Not your tenant."),
        Member => null,
        Key k when request.Scope.Key == k.Tenant => null,
        _ => Error.Forbidden("access.unknown", "Who?"),
    });
}

/// <summary>Stands for a store's refusal (Postgres's read-only or RLS error), classified by the fake unit of work.</summary>
internal sealed class StoreRefusal(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class Store
{
    public List<WorkScope> Begun { get; } = [];
    public List<AuditEntry> Audited { get; } = [];
    public List<uint> Expected { get; } = [];
    public int Commits { get; set; }
    public int Disposed { get; set; }
}

internal sealed class FakeUnitOfWorkFactory(Store store) : IUnitOfWorkFactory
{
    public Error? Classify(Exception exception) => exception is StoreRefusal r ? Error.Forbidden(r.Code, "refused") : null;

    public Task<IUnitOfWork> BeginAsync(WorkScope scope, CancellationToken ct)
    {
        store.Begun.Add(scope);
        return Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(store, scope));
    }
}

internal sealed class FakeUnitOfWork(Store store, WorkScope scope) : IUnitOfWork
{
    private readonly List<AuditEntry> _pending = [];

    public WorkScope Scope => scope;

    public void Expect(uint version) => store.Expected.Add(version);

    public Task AuditAsync(AuditEntry entry, CancellationToken ct)
    {
        _pending.Add(entry);
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct)
    {
        store.Audited.AddRange(_pending);
        store.Commits++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        store.Disposed++;
        return ValueTask.CompletedTask;
    }
}

[Refuses("admission.closed")]
internal sealed class RefuseEverything : IAdmission
{
    public Task<Error?> AdmitAsync(IOperation operation, OperationContext context, CancellationToken ct) =>
        Task.FromResult<Error?>(Error.Forbidden("admission.closed", "Closed."));
}
