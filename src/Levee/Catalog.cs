using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Levee;

/// <summary>One operation's contract: what the pipeline enforces and every projection shows. Built by the generator.</summary>
public abstract class OperationDescriptor
{
    private protected OperationDescriptor(string name, string summary, string access, Effects effects, Idempotency idempotency,
        bool platform, bool @internal, IReadOnlyList<Attribute> metadata, IReadOnlyList<string> errors, Type inputType, Type resultType,
        bool callerScoped = false, bool unscoped = false)
    {
        CallerScoped = callerScoped;
        Unscoped = unscoped;
        Errors = errors;
        Platform = platform;
        Internal = @internal;
        Metadata = metadata;
        Name = name;
        Summary = summary;
        Access = access;
        Effects = effects;
        Idempotency = idempotency;
        InputType = inputType;
        ResultType = resultType;
    }

    public string Name { get; }
    public string Summary { get; }
    public string Access { get; }
    public Effects Effects { get; }
    public Idempotency Idempotency { get; }

    /// <summary>Acts across every scope, as its contract declares.</summary>
    public bool Platform { get; }

    /// <summary>Runs in its caller's scope, the principal's, not its input's.</summary>
    public bool CallerScoped { get; }

    /// <summary>Runs in no one's scope: no tenant's rows, only what every scope shares.</summary>
    public bool Unscoped { get; }

    /// <summary>The platform's own: never projected to a client.</summary>
    public bool Internal { get; }

    /// <summary>A subscription: it answers with a stream (<see cref="ISubscription{TItem}"/>), not once.</summary>
    public bool Streams { get; private protected init; }

    /// <summary>The app's <see cref="OperationMetadataAttribute"/> attributes on the operation.</summary>
    public IReadOnlyList<Attribute> Metadata { get; }

    /// <summary>The codes its handler fails with, as declared; Levee's own (<see cref="LeveeErrors"/>) come on top.</summary>
    public IReadOnlyList<string> Errors { get; }

    public bool Declares(string code) => LeveeErrors.All.Contains(code) || Errors.Contains(code, StringComparer.Ordinal);

    public TMetadata? Get<TMetadata>()
        where TMetadata : Attribute =>
        Metadata.OfType<TMetadata>().FirstOrDefault();

    public Type InputType { get; }
    public Type ResultType { get; }

    /// <summary>Runs in a read-only transaction: a write fails at the database.</summary>
    public bool ReadOnly => (Effects & ~Effects.Read) == Effects.None;

    public bool Audited => !ReadOnly;

    public static OperationDescriptor Create<TOperation, TResult>(string name, string summary, string access, Effects effects,
        Idempotency idempotency, bool platform, bool @internal = false, IReadOnlyList<Attribute>? metadata = null, IReadOnlyList<string>? errors = null,
        bool callerScoped = false, bool unscoped = false)
        where TOperation : class, IOperation<TResult> =>
        new OperationDescriptor<TOperation, TResult>(name, summary, access, effects, idempotency, platform, @internal, metadata ?? [], errors ?? [], callerScoped,
            unscoped);

    /// <summary>A subscription's descriptor. Its result type is the type of each item.</summary>
    public static OperationDescriptor CreateSubscription<TSubscription, TItem>(string name, string summary, string access, bool platform,
        bool @internal = false, IReadOnlyList<Attribute>? metadata = null, bool callerScoped = false, bool unscoped = false)
        where TSubscription : class, ISubscription<TItem> =>
        new SubscriptionDescriptor<TSubscription, TItem>(name, summary, access, platform, @internal, metadata ?? [], callerScoped, unscoped);

    internal virtual Task<Result<object?>> InvokeAsync(IServiceProvider services, IOperation operation, OperationContext context,
        CancellationToken ct) =>
        throw new InvalidOperationException($"{Name} streams: subscribe to it.");

    internal virtual IAsyncEnumerable<object?> SubscribeAsync(IServiceProvider services, IOperation operation, SubscriptionContext context,
        CancellationToken ct) =>
        throw new InvalidOperationException($"{Name} answers once: send it.");
}

internal sealed class SubscriptionDescriptor<TSubscription, TItem> : OperationDescriptor
    where TSubscription : class, ISubscription<TItem>
{
    public SubscriptionDescriptor(string name, string summary, string access, bool platform, bool @internal, IReadOnlyList<Attribute> metadata,
        bool callerScoped, bool unscoped)
        : base(name, summary, access, Effects.Read, Idempotency.Safe, platform, @internal, metadata, [], typeof(TSubscription), typeof(TItem), callerScoped,
            unscoped) =>
        Streams = true;

    internal override async IAsyncEnumerable<object?> SubscribeAsync(IServiceProvider services, IOperation operation, SubscriptionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in services.GetRequiredService<ISubscriptionHandler<TSubscription, TItem>>().SubscribeAsync((TSubscription)operation, context, ct)
            .WithCancellation(ct))
        {
            yield return item;
        }
    }
}

internal sealed class OperationDescriptor<TOperation, TResult>(string name, string summary, string access, Effects effects, Idempotency idempotency,
    bool platform, bool @internal, IReadOnlyList<Attribute> metadata, IReadOnlyList<string> errors, bool callerScoped, bool unscoped)
    : OperationDescriptor(name, summary, access, effects, idempotency, platform, @internal, metadata, errors, typeof(TOperation), typeof(TResult), callerScoped,
        unscoped)
    where TOperation : class, IOperation<TResult>
{
    internal override async Task<Result<object?>> InvokeAsync(IServiceProvider services, IOperation operation, OperationContext context,
        CancellationToken ct)
    {
        var result = await services.GetRequiredService<IOperationHandler<TOperation, TResult>>().HandleAsync((TOperation)operation, context, ct);
        return result.IsSuccess ? Result<object?>.Success(result.Value) : result.Error;
    }
}

/// <summary>Operations a composition contributes to the catalog: the app's, then a test's own on top.</summary>
public sealed class OperationCatalogPart(IReadOnlyList<OperationDescriptor> operations, IReadOnlyList<string>? refusals = null)
{
    public IReadOnlyList<OperationDescriptor> Operations { get; } = operations;

    public IReadOnlyList<string> Refusals { get; } = refusals ?? [];
}

/// <summary>Every operation the app offers. The dispatcher, the projections and the jobs all read this one list.</summary>
public sealed class OperationCatalog
{
    private readonly FrozenDictionary<string, OperationDescriptor> _byName;
    private readonly FrozenDictionary<Type, OperationDescriptor> _byType;

    /// <param name="operations">Every operation.</param>
    /// <param name="refusals">The codes the access policy and admissions refuse with, before any operation runs (<see cref="RefusesAttribute"/>).</param>
    public OperationCatalog(IEnumerable<OperationDescriptor> operations, IEnumerable<string>? refusals = null)
    {
        Refusals = [.. (refusals ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var list = operations.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        if (list.GroupBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"Two operations are named {duplicate.Key}.");
        }
        // Every contract proven against the wire where the catalog is made (a host's start, the generated catalog test), not
        // when a caller first meets it: one the wire refuses would otherwise fail its calls, and every listing of the catalog.
        Prove(list.Select(o => (o.Name, o.InputType, true, o.InputType.Assembly))
            .Concat(list.Select(o => (o.Name, o.ResultType, false, o.InputType.Assembly))));
        _byName = list.ToFrozenDictionary(o => o.Name, StringComparer.Ordinal);
        _byType = list.ToFrozenDictionary(o => o.InputType);
        All = list;
    }

    /// <param name="contracts">Each contract type, by the operation it belongs to.</param>
    /// <param name="inputs">Read from callers, so also proven to keep every value read; a result is only proven readable.</param>
    internal static void Prove(IEnumerable<(string Operation, Type Contract)> contracts, bool inputs = true) =>
        Prove(contracts.Select(c => (c.Operation, c.Contract, inputs, c.Contract.Assembly)));

    /// <param name="contracts">Each contract type by its operation, whether it's read from callers, and the assembly whose own records it may carry: the operation's.</param>
    private static void Prove(IEnumerable<(string Operation, Type Contract, bool Input, System.Reflection.Assembly Owner)> contracts)
    {
        var refused = contracts.Select(c => (c.Operation, c.Contract, Problem: Unwired(c.Contract, c.Input, c.Owner))).Where(c => c.Problem is not null).ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                refused.Select(c => $"{c.Operation}'s {c.Contract.Name} isn't one the contract wire carries: {c.Problem}")));
        }
    }

    private static string? Unwired(Type type, bool input, System.Reflection.Assembly owner)
    {
        try
        {
            LeveeJson.Schema(type);
            return Outside(type, input, owner);
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    private static string MetadataName(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name;

    /// <summary>
    /// The first type a contract carries outside the contract language, with why, or null: the build's LEVEE104 again, as the
    /// wire's own metadata sees it, for whatever the build didn't. An allow-list, so a shape nobody thought of is refused, not
    /// published: scalars, enums, value objects, the collections above, objects the wire constructs (an input's every member
    /// read, not only written), and abstract types whose every subtype has a discriminator.
    /// </summary>
    private static string? Outside(Type root, bool input, System.Reflection.Assembly owner)
    {
        var seen = new HashSet<Type>();
        var next = new Stack<Type>([root]);
        while (next.TryPop(out var type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type) || type.IsEnum || ContractLanguage.Scalars.Contains(MetadataName(type)) || LeveeJson.IsValueObject(type))
            {
                continue;
            }
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                {
                    return $"{type.Name} is a many-dimensional array, which JSON has no shape for. Fix: an array of arrays (LEVEE104).";
                }
                next.Push(type.GetElementType()!);
                continue;
            }
            if (type.IsGenericType && ContractLanguage.Collections.Contains(MetadataName(type)))
            {
                var arguments = type.GetGenericArguments();
                if (arguments.Length == 2 && !(arguments[0].IsEnum || ContractLanguage.Key(MetadataName(arguments[0]))))
                {
                    return $"{type.Name} is keyed by {arguments[0].Name}, which isn't in the contract language. Fix: key it by a string, number, Guid, date or enum (LEVEE104).";
                }
                foreach (var argument in arguments)
                {
                    next.Push(argument);
                }
                continue;
            }
            var language = " Fix: a contract is made of what the wire reads back as it writes it: scalars, enums, value objects, arrays and "
                + "List<T>-like collections, string-keyed dictionaries, positional records, and abstract records whose every subtype has a "
                + "discriminator (LEVEE104).";
            if (type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal)))
            {
                return $"{type.Name} isn't in the contract language." + language;
            }
            if (type.IsInterface)
            {
                return $"{type.Name} is an interface, which the wire writes but can't read back." + language;
            }
            // Levee's own (ChangeSignal) are judged by Levee's tests.
            if (ContractLanguage.LeveeRecords.Contains(MetadataName(type)))
            {
                continue;
            }
            for (var baseOf = type.BaseType; baseOf is not null && baseOf != typeof(object) && baseOf != typeof(ValueType); baseOf = baseOf.BaseType)
            {
                if (baseOf.Assembly != owner)
                {
                    return $"{type.Name} derives from {baseOf.Name}, {baseOf.Assembly.GetName().Name}'s, so how its values reach their members can't be judged "
                        + "where the contract is. Fix: declare the contract's own record, with no base from another assembly (LEVEE104).";
                }
            }
            if (type.Assembly != owner)
            {
                return $"{type.Name} is {type.Assembly.GetName().Name}'s, not the contract's own, so how it's built can't be judged where the contract is. "
                    + "Fix: declare the contract's own record beside the operation, or carry the value as a value object or an enum (LEVEE104).";
            }
            var info = LeveeJson.Options.GetTypeInfo(type);
            if (info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
            {
                return $"{type.Name} isn't in the contract language." + language;
            }
            if (info.PolymorphismOptions is { } dropping && (dropping.IgnoreUnrecognizedTypeDiscriminators
                || dropping.UnknownDerivedTypeHandling != System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FailSerialization))
            {
                return $"{type.Name} drops what it doesn't recognize ([JsonPolymorphic] ignoring an unknown discriminator, or writing an undeclared "
                    + "subtype as its base), so a caller's subtype is lost, not refused. Fix: leave [JsonPolymorphic] its defaults (LEVEE104).";
            }
            if (type.IsAbstract && info.PolymorphismOptions is not { DerivedTypes.Count: > 0 }
                || info.PolymorphismOptions is { } some && some.DerivedTypes.Any(d => d.TypeDiscriminator is null))
            {
                return $"{type.Name} is read as one of its subtypes only by a discriminator each declares." + language;
            }
            // Each declared subtype is a shape the wire carries here, whether its base is abstract or not.
            foreach (var derived in info.PolymorphismOptions?.DerivedTypes ?? [])
            {
                next.Push(derived.DerivedType);
            }
            if (type.IsAbstract)
            {
                continue;
            }
            if (info.CreateObject is null && info.ConstructorAttributeProvider is null)
            {
                return $"{type.Name} has no constructor the wire can call, so it writes it but can't read it back. Fix: a positional record (LEVEE104).";
            }
            if (info.ConstructorAttributeProvider is System.Reflection.MethodBase constructor
                && constructor.GetParameters().FirstOrDefault(p => !info.Properties.Any(m => m.AssociatedParameter?.Name == p.Name)) is { } unbound)
            {
                return $"{type.Name}'s constructor takes {unbound.Name}, which no member of its name carries, so the wire can't read it. Fix: a positional record (LEVEE104).";
            }
            if (input && info.Properties.FirstOrDefault(p => p.Set is null && p.AssociatedParameter is null) is { } writeOnly)
            {
                return $"{type.Name}.{writeOnly.Name} is a member the wire writes but never reads, so a caller's value for it is dropped, not refused. "
                    + "Fix: take it as a positional parameter, or move it off the input (LEVEE104).";
            }
            foreach (var member in info.Properties)
            {
                next.Push(member.PropertyType);
            }
        }
        return null;
    }

    public IReadOnlyList<OperationDescriptor> All { get; }

    /// <summary>What any operation may be refused with before it runs: the access policy's and admissions' declared codes.</summary>
    public IReadOnlyList<string> Refusals { get; }

    public OperationDescriptor? Find(string name) => _byName.GetValueOrDefault(name);

    public OperationDescriptor? Find(Type inputType) => _byType.GetValueOrDefault(inputType);

    // refused: told of an exception the input's own type threw that isn't a validation's, so its message stays in the log.
    internal static Result<IOperation> Deserialize(OperationDescriptor descriptor, JsonElement input, JsonSerializerOptions json, Action<Exception>? refused = null)
    {
        try
        {
            var operation = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize("{}", descriptor.InputType, json)
                : input.Deserialize(descriptor.InputType, json);
            return operation is IOperation op
                ? Result<IOperation>.Success(op)
                : Error.Validation(LeveeErrors.InvalidInput, $"{descriptor.Name}: no input.");
        }
        catch (JsonException e)
        {
            return Error.InvalidInput(e, descriptor.Name);
        }
        // A record that validates in its constructor, or an app's converter (a typed id), refusing the client's value.
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            return Error.InvalidInput(new JsonException(e.Message, "$", null, null, e), descriptor.Name);
        }
        // Anything else it throws (a value object's own exception, a bug) still refuses the input, but its message may hold
        // anything, so the client hears only that, and the log the rest.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            refused?.Invoke(e);
            return Error.Validation(LeveeErrors.InvalidInput, $"{descriptor.Name}: its input was refused.");
        }
    }
}
