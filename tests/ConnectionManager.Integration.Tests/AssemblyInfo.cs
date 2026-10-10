// These tests change machine-wide state (WFP block-all, policy registry values, QoS), so they must not overlap:
// one test's block-all would cut off another test's download.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
