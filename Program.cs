using Prometheus;
using ShakeDetectService;



Console.CancelKeyPress += (s, e) =>
    {
      Console.WriteLine("Stopping...");
      Environment.Exit(0);
    };

var server = new MetricServer(13543);
server.Start();

var watchService = new KyoshinMonitorWatchService();
watchService.Start();

await Task.Delay(-1);
