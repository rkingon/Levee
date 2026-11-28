using Levee;
using Sample.Contracts;
using Sample.Domain;

namespace Sample.Application;

/// <summary>
/// Words this app's operations refuse in a note's text: a moderation policy of the app's, which list a setting picks, not an
/// invariant of the note. The port doesn't know where the words come from.
/// </summary>
[Port(SelectedBy = "Sample:BannedWords")]
public interface IBannedWords
{
    bool Contains(string text);
}

public interface INotes : IRepository<Note>
{
    void Add(Note note);

    Task<Note?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>The unit of work's scope decides whose notes these are.</summary>
    Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct);
}

internal sealed class CreateNoteHandler(INotes notes, IBannedWords banned, IFlush flush) : IOperationHandler<CreateNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(CreateNote operation, OperationContext context, CancellationToken ct)
    {
        var text = NoteTexts.Allowed(operation.Text, banned);
        if (!text.IsSuccess)
        {
            return text.Error;
        }
        var note = Note.Create(operation.Id, operation.Tenant, text.Value, context.Now);
        notes.Add(note);
        // Written inside the operation, so the view carries the version just written.
        await flush.FlushAsync(ct);
        return View(note);
    }

    internal static NoteView View(Note note) => new(note.Id, note.Text, note.CreatedAt, note.Version);
}

internal sealed class EditNoteHandler(INotes notes, IBannedWords banned, IFlush flush) : IOperationHandler<EditNote, NoteView>
{
    public async Task<Result<NoteView>> HandleAsync(EditNote operation, OperationContext context, CancellationToken ct)
    {
        var text = NoteTexts.Allowed(operation.Text, banned);
        if (!text.IsSuccess)
        {
            return text.Error;
        }
        if (await notes.FindAsync(operation.Id, ct) is not { } note)
        {
            return Error.NotFound("note.missing", "There's no such note.");
        }
        note.Edit(text.Value);
        // Written inside the operation, so the view carries the version just written.
        await flush.FlushAsync(ct);
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

/// <summary>The text an operation sets on a note: the domain's own rule for it, then this app's policy on words.</summary>
internal static class NoteTexts
{
    public static Result<NoteText> Allowed(string text, IBannedWords banned)
    {
        var held = NoteText.From(text);
        return held.IsSuccess && banned.Contains(held.Value.Value) ? Error.Validation("note.banned", "That note holds a banned word.") : held;
    }
}
