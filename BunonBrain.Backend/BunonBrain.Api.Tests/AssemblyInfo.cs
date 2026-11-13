// Test hosts read their configuration from process environment variables
// (see TestEnvironment), and the API keeps static simulation state, so test
// collections must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
