using ConnectionManager.Core.Wfp;

// Test helper: blocks one program through a dynamic WFP session, prints READY, then waits to be killed.
// CrashSafetyTests kills it to prove Windows removes the filters when the owning process dies.
if (args is not ["--app", var path])
{
    Console.Error.WriteLine("usage: ConnectionManager.WfpProbe --app <exe path>");
    return 2;
}

using var engine = new WfpEngine("cm-probe");
engine.InTransaction(() => engine.Add(new FilterRule(RuleKind.App, Path.GetFullPath(path), RuleAction.Block, RuleWeight.GameBlock, "probe")));
Console.WriteLine("READY");
Thread.Sleep(Timeout.Infinite);
return 0;
