// See https://aka.ms/new-console-template for more information
if (args.Contains("--idle")) { await Task.Delay(Timeout.Infinite); return; }
await IntegrationChecks.RunAsync();
