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
