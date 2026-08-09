using System.Text;

Console.InputEncoding = new UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false,
    throwOnInvalidBytes: true);
Console.OutputEncoding = new UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false,
    throwOnInvalidBytes: true);

var line = await Console.In.ReadLineAsync();
if (line is not null)
{
    await Console.Out.WriteLineAsync(line);
    await Console.Out.FlushAsync();
}

public sealed class UnicodeEchoFixtureMarker;
