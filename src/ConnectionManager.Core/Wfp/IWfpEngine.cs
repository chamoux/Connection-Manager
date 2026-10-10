namespace ConnectionManager.Core.Wfp;

public interface IWfpEngine : IDisposable
{
    /// Adds the rule's filter on every layer the app uses and returns their ids.
    /// If it throws, it has added nothing: any filters it managed to add are removed first.
    IReadOnlyList<ulong> Add(FilterRule rule);

    /// Deletes one filter. A filter that is already gone is not an error.
    void DeleteFilter(ulong filterId);

    /// Runs the work once, as one transaction (not nestable). If the work or the commit fails, all of it is rolled back.
    void InTransaction(Action work);
}

public sealed class WfpException(string operation, uint code) : Exception($"{operation} failed with 0x{code:X8}")
{
    public string Operation { get; } = operation;
    public uint Code { get; } = code;
}
