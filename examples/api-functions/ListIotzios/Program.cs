using Com.Iotzio.Api;

var iotzioManager = new IotzioManager();
var iotzioInfos = iotzioManager.ListConnectedBoards();

foreach (var iotzioInfo in iotzioInfos)
{
    using var iotzio = iotzioInfo.Open();

    Console.WriteLine($"Found Iotzio {iotzio.Version()} with serial number {iotzio.SerialNumber()}!");
}