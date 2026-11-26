using Levee;

namespace Sample.Contracts;

public sealed record NoteView(Guid Id, string Text, DateTimeOffset CreatedAt);

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
