namespace Levee;

/// <summary>An assembly's role, emitted by Levee's generator from the project's <c>LeveeRole</c>. Read by the role rules (LEVEE002).</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class LeveeRoleAttribute(string role) : Attribute
{
    public string Role { get; } = role;
}

/// <summary>Generated: the type whose <c>Register(IServiceCollection)</c> adds this assembly's handlers and adapters.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class LeveeRegistrationAttribute(Type registration) : Attribute
{
    public Type Registration { get; } = registration;
}

/// <summary>Generated: one per handler in the assembly, naming the operation it handles.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class LeveeHandlesAttribute(Type operation) : Attribute
{
    public Type Operation { get; } = operation;
}

/// <summary>Generated: one per port an adapter in the assembly implements, with the adapter's name if it has one, and its lifetime.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class LeveeAdaptsAttribute(Type port, string? name, AdapterLifetime lifetime) : Attribute
{
    public Type Port { get; } = port;
    public string? Name { get; } = name;
    public AdapterLifetime Lifetime { get; } = lifetime;
}

/// <summary>Generated: one per code an access policy or admission in the assembly declares it refuses with.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class LeveeRefusesAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}
