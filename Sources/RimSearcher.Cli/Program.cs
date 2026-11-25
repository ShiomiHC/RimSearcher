using System.Text;
using RimSearcher.Cli;

Console.OutputEncoding = new UTF8Encoding(false);

return Runner.Run(args, Console.Out, Console.Error);
