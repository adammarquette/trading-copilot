// SERIAL, deliberately. Every construct call goes through the jsii runtime, which extracts CDK
// tarballs on first use. xUnit's default runs test classes in parallel, so two fixtures
// initialising at once race on that extraction (the pattern library's Infra.Tests hit this).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
