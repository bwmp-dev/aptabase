// Trusted test executable, mounted explicitly by the test administrator, never uploaded.
var scenario = File.ReadAllBytes("/input/artifact.elf")[0];
switch (scenario)
{
    case 1:
        await Task.Delay(TimeSpan.FromMinutes(1));
        break;
    case 2:
        var chunk = new string('x', 4096);
        for (var i = 0; i < 4097; i++) Console.Write(chunk);
        break;
    case 3:
        Console.Write("not json");
        break;
    case 4:
        return 2;
    case 5:
        Console.Write("{\"BuildId\":null,\"Functions\":[]}");
        break;
}
return 0;
