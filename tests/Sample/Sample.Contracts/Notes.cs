using Levee;

namespace Sample.Contracts;

public sealed record NoteView(Guid Id, string Text, DateTimeOffset CreatedAt, uint Version);

/// <summary>The caller picks the id, so a repeat (a retried request, a job run twice) conflicts instead of writing a second note.</summary>
[Operation("notes.create", "member", Summary = "Writes a note.", Effects = Effects.Write, Idempotency = Idempotency.Conflicts,
    Errors = ["note.empty", "note.banned"])]
public sealed record CreateNote(Guid Id, string Tenant, string Text) : IOperation<NoteView>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("notes.list", "member", Summary = "The tenant's notes, oldest first.")]
public sealed record ListNotes(string Tenant) : IOperation<IReadOnlyList<NoteView>>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

[Operation("notes.create_later", "member", Summary = "Writes a note in a job.", Effects = Effects.Write, Idempotency = Idempotency.None)]
public sealed record CreateNoteLater(string Tenant, string Text) : IOperation<Unit>, IScopedOperation
{
    string IScopedOperation.ScopeKey => Tenant;
}

/// <summary>Held to the version its caller saw: an edit made on a note someone else has changed since is refused, not lost.</summary>
[Operation("notes.edit", "member", Summary = "Rewrites a note.", Effects = Effects.Write, Idempotency = Idempotency.Idempotent,
    Errors = ["note.empty", "note.missing"])]
public sealed record EditNote(Guid Id, string Tenant, string Text, uint Version) : IOperation<NoteView>, IScopedOperation, IVersionedOperation
{
    string IScopedOperation.ScopeKey => Tenant;

    uint IVersionedOperation.ExpectedVersion => Version;
}
