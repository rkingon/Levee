using Levee;
using Levee.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sample.Application;
using Sample.Domain;

namespace Sample.Adapters;

public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public const string NotesTable = "notes";

    /// <summary>What the schema's owner runs; the RLS policy is Levee's.</summary>
    public static string Sql { get; } = $"""
        create table {NotesTable} (id uuid primary key, tenant text not null, text text not null, created_at timestamptz not null);
        {LeveeSchema.Isolation(NotesTable, "tenant")}
        """;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddLeveeAudit();
        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable(NotesTable);
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.Tenant).HasColumnName("tenant");
            e.Property(n => n.Text).HasColumnName("text");
            e.Property(n => n.CreatedAt).HasColumnName("created_at");
        });
    }
}

internal sealed class EfNotes(SampleDbContext db) : INotes
{
    public void Add(Note note) => db.Set<Note>().Add(note);

    public async Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct) =>
        await db.Set<Note>().OrderBy(n => n.CreatedAt).ToListAsync(ct);
}

[Adapter("none")]
internal sealed class NoBannedWords : IBannedWords
{
    public bool Contains(string text) => false;
}

[Adapter("list")]
internal sealed class ListedBannedWords : IBannedWords
{
    private static readonly string[] Words = ["spam"];

    public bool Contains(string text) => Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}
