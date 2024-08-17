using WebSocketSharp.Server;
using Prometheus;
using ShakeDetectService;
using WebSocketSharp;



Console.CancelKeyPress += (s, e) =>
    {
      Console.WriteLine("Stopping...");
      Environment.Exit(0);
    };

var metricserver = new MetricServer(8182);
metricserver.Start();


// Websocket server
var wssv = new WebSocketServer(8181);
wssv.AddWebSocketService<Echo>("/");
wssv.Start();

var watchService = new KyoshinMonitorWatchService(wssv);
watchService.Start();


await Task.Delay(-1);


public class Echo : WebSocketBehavior
{
  protected override void OnMessage(MessageEventArgs e)
  {
    Console.WriteLine("Received: " + e.Data);
  }
}
