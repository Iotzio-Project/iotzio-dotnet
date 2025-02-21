using Com.Iotzio.Api;

var iotzioManager = new IotzioManager();
var iotzioInfos = iotzioManager.ListConnectedBoards();

if (!iotzioInfos.Any())
{
    Console.WriteLine("No Iotzio found!");
}
else
{
    foreach (var iotzioInfo in iotzioInfos)
    {
        using var iotzio = iotzioInfo.Open();

        Console.WriteLine($"Found Iotzio {iotzio.Version()} with serial number {iotzio.SerialNumber()}!");
    }
}
