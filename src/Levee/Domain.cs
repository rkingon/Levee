namespace Levee;

/// <summary>An aggregate's root: the only thing a repository loads or saves, and changed only through its own methods (LEVEE007).</summary>
public interface IAggregateRoot;

/// <summary>A repository exists only for an aggregate root. It's a port: declared in Application, implemented by an adapter.</summary>
public interface IRepository<TAggregate>
    where TAggregate : class, IAggregateRoot;
