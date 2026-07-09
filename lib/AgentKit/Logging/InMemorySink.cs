namespace AgentKit.Logging;

/// <summary>Collects records in memory — for tests and for the eval CLI's token/cost correlation.</summary>
public sealed class InMemorySink : ICompletionSink
{
    private readonly List<CompletionRecord> _records = [];

    public IReadOnlyList<CompletionRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public void Record(CompletionRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    public void Clear()
    {
        lock (_records)
        {
            _records.Clear();
        }
    }
}
