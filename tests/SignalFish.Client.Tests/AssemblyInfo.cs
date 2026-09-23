/*
    Fixture-level test parallelization: NUnit defaults to one worker, which
    left ~2/3 of the suite's wall time on the table (17 s sequential vs ~7 s
    parallel for 500+ tests on 8 workers). Every fixture is self-contained
    (own transports, clocks, ephemeral ports) and the suite stays green and
    deterministic under the parallel adapter — pinned by the full run on
    both target frameworks.
*/
using NUnit.Framework;

[assembly: Parallelizable(ParallelScope.Fixtures)]
