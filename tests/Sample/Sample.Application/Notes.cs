using Levee;
using Sample.Contracts;
using Sample.Domain;

namespace Sample.Application;

/// <summary>Words a note may not hold. Which list applies is a setting: the port doesn't know where the words come from.</summary>
[Port(SelectedBy = "Sample:BannedWords")]
public interface IBannedWords
{
    bool Contains(string text);
}

public interface INotes : IRepository<Note>
{
    void Add(Note note);

    Task<Note?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Writes what changed, inside the operation, so the note's version is the one just written.</summary>
    Task SaveAsync(CancellationToken ct);

    /// <summary>The unit of work's scope decides whose notes these are.</summary>
    Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct);
}

internal sealed class CreateNoteHandler(INotes notes, IBannedWords banned) : IOperationHandler<CreateNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(CreateNote operation, OperationContext context, CancellationToken ct)
    {
        if (NoteText.Refusal(operation.Text, banned) is { } refused)
        {
            return refused;
        }
        var note = Note.Create(operation.Id, operation.Tenant, operation.Text, context.Now);
        notes.Add(note);
        await notes.SaveAsync(ct);
        return View(note);
    }

    internal static NoteView View(Note note) => new(note.Id, note.Text, note.CreatedAt, note.Version);
}

internal sealed class EditNoteHandler(INotes notes, IBannedWords banned) : IOperationHandler<EditNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(EditNote operation, OperationContext context, CancellationToken ct)
    {
        if (NoteText.Refusal(operation.Text, banned) is { } refused)
        {
            return refused;
        }
        if (await notes.FindAsync(operation.Id, ct) is not { } note)
        {
            return Error.NotFound("note.missing", "There's no such note.");
        }
        note.Edit(operation.Text);
        await notes.SaveAsync(ct);
        return CreateNoteHandler.View(note);
    }
}

internal sealed class ListNotesHandler(INotes notes) : IOperationHandler<ListNotes, IReadOnlyList<NoteView>>
{
    public async Task<Result<IReadOnlyList<NoteView>>> HandleAsync(ListNotes operation, OperationContext context, CancellationToken ct) =>
        (await notes.ListAsync(ct)).Select(CreateNoteHandler.View).ToList();
}

internal sealed class CreateNoteLaterHandler(IJobs jobs) : IOperationHandler<CreateNoteLater, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CreateNoteLater operation, OperationContext context, CancellationToken ct)
    {
        var queued = await jobs.EnqueueAsync(new CreateNote(context.NewId(), operation.Tenant, operation.Text), null, ct);
        return queued.IsSuccess ? Unit.Value : queued.Error;
    }
}

/// <summary>What a note's text must be, written once and held alike wherever text is set: a note is never written as one that isn't.</summary>
internal static class NoteText
{
    public static Error? Refusal(string text, IBannedWords banned) =>
        string.IsNullOrWhiteSpace(text) ? Error.Validation("note.empty", "A note needs text.")
        : banned.Contains(text) ? Error.Validation("note.banned", "That note holds a banned word.")
        : null;
}
