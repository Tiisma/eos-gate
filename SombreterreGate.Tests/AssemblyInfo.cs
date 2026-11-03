using Xunit;

// Atlas runs one embedded server per test class, so classes must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
