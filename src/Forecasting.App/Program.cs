using Forecasting.App;

var request = PipelineCliRequestParser.BuildRequest(args);
if (request is null)
{
    Console.WriteLine("Unknown mode. Use one of: all, part1, part2, part3, part4, diagnostics.");
    return;
}

PipelineRunner.Run(request, Console.WriteLine);
