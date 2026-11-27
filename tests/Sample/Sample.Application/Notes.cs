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

    /// <summary>The unit of work's scope decides whose notes these are.</summary>
    Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct);
}

internal sealed class CreateNoteHandler(INotes notes, IBannedWords banned) : IOperationHandler<CreateNote, NoteView>
{
    public Task<Result<NoteView>> HandleAsync(CreateNote operation, OperationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.Text))
        {
            return Task.FromResult<Result<NoteView>>(Error.Validation("note.empty", "A note needs text."));
        }
        if (banned.Contains(operation.Text))
        {
            return Task.FromResult<Result<NoteView>>(Error.Validation("note.banned", "That note holds a banned word."));
        }
        var note = Note.Create(operation.Id, operation.Tenant, operation.Text, context.Now);
        notes.Add(note);
        return Task.FromResult<Result<NoteView>>(View(note));
    }

    internal static NoteView View(Note note) => new(note.Id, note.Text, note.CreatedAt);
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
