namespace ShakeDetectService;

class Program
{

  static async Task Main(string[] args)
  {
    Console.CancelKeyPress += (s, e) =>
    {
      Console.WriteLine("Stopping...");
      Environment.Exit(0);
    };

    Console.WriteLine("Hello World!");
    var watchService = new KyoshinMonitorWatchService();
    watchService.Start();

    await Task.Delay(-1);
  }
}
