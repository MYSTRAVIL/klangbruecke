using Xunit;

// Log.Current is process-wide state that LogTests swaps, and FileLogTests swaps CurrentCulture.
// xunit runs separate test classes as parallel collections by default, which would race on both.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
