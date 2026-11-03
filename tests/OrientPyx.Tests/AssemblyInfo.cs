// Every test shares one data root and one headless application instance (see Support/TestEnvironment.cs and
// Support/HeadlessApp.cs), so the tests run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
