// Usage: dotnet run --project tools\Pack -- <starter exe> <published app folder> <output exe>
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Pack <starter exe> <published app folder> <output exe>");
    return 2;
}
Queueify.Launcher.Bundle.Write(args[0], args[1], args[2]);
return 0;
