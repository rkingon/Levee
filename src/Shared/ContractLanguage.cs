namespace Levee;

/// <summary>
/// The contract language's types, by metadata name: one definition, compiled into both LEVEE104 and the catalog's check at
/// start, so what the build allows and what a host starts with can't drift apart.
/// </summary>
internal static class ContractLanguage
{
    /// <summary>Values the wire writes as one JSON scalar and reads back as written.</summary>
    public static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "System.String", "System.Char", "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32",
        "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal", "System.Guid", "System.DateOnly",
        "System.TimeOnly", "System.TimeSpan", "System.DateTime", "System.DateTimeOffset", "System.Uri", "System.Version", "Levee.Unit",
    };

    /// <summary>Collections the wire builds back as it writes them: one dimension, and a dictionary by a <see cref="Key"/>.</summary>
    public static readonly HashSet<string> Collections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1", "System.Collections.Generic.IList`1", "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.HashSet`1", "System.Collections.Generic.ISet`1", "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.IDictionary`2", "System.Collections.Generic.IReadOnlyDictionary`2",
    };

    /// <summary>Levee's own records a contract may carry, judged by Levee's tests.</summary>
    public static readonly HashSet<string> LeveeRecords = new(StringComparer.Ordinal) { "Levee.ChangeSignal" };

    /// <summary>The System.Text.Json attributes a contract's member may carry: each says the same to the schema and the wire.</summary>
    public static readonly HashSet<string> MemberAttributes = new(StringComparer.Ordinal) { "JsonPropertyNameAttribute" };

    /// <summary>A dictionary key: a scalar the wire writes as a property name (Unit has none), or an enum.</summary>
    public static bool Key(string name) => name != "Levee.Unit" && Scalars.Contains(name);
}
