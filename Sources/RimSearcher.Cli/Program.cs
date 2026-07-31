using System.Text;
using RimSearcher.Cli;

// stdout 一律 UTF-8 无 BOM:输出里有中文 label 与译文,控制台默认编码会把它们变成问号,
// 而调用方看到问号只会以为数据本身是坏的。
Console.OutputEncoding = new UTF8Encoding(false);

return Runner.Run(args, Console.Out, Console.Error);
