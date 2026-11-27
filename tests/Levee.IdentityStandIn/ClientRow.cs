namespace Levee.IdentityStandIn;

public sealed class ClientRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Tenant { get; set; } = "";
}
