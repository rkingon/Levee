using Levee;

namespace Sample.Domain;

public sealed class Note : IAggregateRoot
{
    private Note(Guid id, string tenant, string text, DateTimeOffset createdAt)
    {
        Id = id;
        Tenant = tenant;
        Text = text;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Tenant { get; private set; }
    public string Text { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The store's, as of the last write: what an edit names to be held to what its caller saw.</summary>
    public uint Version { get; private set; }

    public static Note Create(Guid id, string tenant, string text, DateTimeOffset now) => new(id, tenant, text.Trim(), now);

    public void Edit(string text) => Text = text.Trim();
}
