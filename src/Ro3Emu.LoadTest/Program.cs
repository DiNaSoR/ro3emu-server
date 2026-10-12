using Ro3Emu.LoadTest;

try
{
    var options = RunnerOptions.Parse(args);
    var report = await new ScenarioRunner(options).RunAsync(CancellationToken.None);
    Console.WriteLine($"{report.Scenario}: {report.Verdict}; {report.AbortReason ?? "completed"}");
    Console.WriteLine($"Report: {options.OutputPrefix}.json / .md");
    Environment.ExitCode = report.Verdict == "PASS" ? 0 : 2;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    Environment.ExitCode = 1;
}
