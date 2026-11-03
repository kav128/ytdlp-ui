using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

switch (args[0])
{
    case "echo": Console.WriteLine(JsonSerializer.Serialize(args.Skip(1))); break;
    case "flood":
        for (var index = 0; index < 12000; index++)
        {
            Console.WriteLine(new string('o', 100));
            Console.Error.WriteLine(new string('e', 100));
        }

        Environment.ExitCode = 7;

        break;
    case "exit-line": Console.WriteLine("ready"); break;
    case "tree":
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
                 {
                     "exec",
                     "--runtimeconfig",
                     args[2],
                     Assembly.GetExecutingAssembly().Location,
                     "child",
                     args[1]
                 }) start.ArgumentList.Add(argument);

        using (var child = Process.Start(start)!)
        {
            Console.WriteLine("child:" + child.Id);
            await Task.Delay(Timeout.Infinite);
        }

        break;
    case "child":
        while (true)
        {
            await File.AppendAllTextAsync(args[1], "tick\n");
            await Task.Delay(50);
        }
}

public sealed class ProcessFixtureMarker;
