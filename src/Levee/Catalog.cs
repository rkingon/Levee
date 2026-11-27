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
        Prove(list.SelectMany(o => new[] { o.InputType, o.ResultType }.Distinct().Select(type => (o.Name, type))));
        _byName = list.ToFrozenDictionary(o => o.Name, StringComparer.Ordinal);
        _byType = list.ToFrozenDictionary(o => o.InputType);
        All = list;
    }

    internal static void Prove(IEnumerable<(string Operation, Type Contract)> contracts)
    {
        var refused = contracts.Select(c => (c.Operation, c.Contract, Problem: Unwired(c.Contract))).Where(c => c.Problem is not null).ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                refused.Select(c => $"{c.Operation}'s {c.Contract.Name} isn't one the contract wire carries: {c.Problem}")));
        }
    }

    private static string? Unwired(Type type)
    {
        try
        {
            LeveeJson.Schema(type);
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
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
