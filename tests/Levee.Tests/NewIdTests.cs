namespace Levee.Tests;

public sealed class NewIdTests
{
    private static OperationContext At(DateTimeOffset now) =>
        new(new Member("ada", "acme"), Generated.LeveeComposition.Catalog.Find(typeof(Echo))!, new WorkScope(null, true), now);

    [Fact]
    public void Ids_minted_in_one_operation_sort_in_the_order_they_were_made()
    {
        var context = At(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

        var ids = Enumerable.Range(0, 10_000).Select(_ => context.NewId()).ToList();

        Assert.Equal(ids.Select(Bytes), ids.Select(Bytes).Order(StringComparer.Ordinal));
        // Past the 12 bits RFC 9562's counter has on its own: the time stays the operation's.
        Assert.All(ids, id => Assert.Equal(context.Now.ToUnixTimeMilliseconds(), Convert.ToInt64(Bytes(id)[..12], 16)));
        Assert.All(ids, id => Assert.Equal(7, id.Version));
        Assert.All(ids, id => Assert.Equal(0b10, id.Variant >> 2));
    }

    [Fact]
    public void An_id_carries_the_operations_time()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, 123, TimeSpan.Zero);

        var id = At(now).NewId();

        Assert.Equal(now.ToUnixTimeMilliseconds(), Convert.ToInt64(Bytes(id)[..12], 16));
    }

    // As Postgres orders a uuid: its bytes, as written.
    private static string Bytes(Guid id) => Convert.ToHexString(id.ToByteArray(bigEndian: true));
}

public sealed class NewIdAcrossOperationsTests
{
    [Fact]
    public void Operations_in_one_millisecond_dont_mint_the_same_first_id()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var descriptor = Generated.LeveeComposition.Catalog.Find(typeof(Echo))!;

        var ids = Enumerable.Range(0, 500_000).Select(_ => new OperationContext(new Member("ada", "acme"), descriptor, new WorkScope(null, true), now).NewId());

        Assert.Equal(500_000, ids.Distinct().Count());
    }
}
