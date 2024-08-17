using KyoshinEewViewer.Core.Models;
using KyoshinMonitorLib;

public class KyoshinEventTelegram
{
  public KyoshinEventTelegram(KyoshinEvent ev)
  {
    id = ev.Id;
    level = ev.Level;
    createdAt = ev.CreatedAt;
    pointCount = ev.PointCount;
    maxIntensity = ev.Points.Max(p => p.LatestIntensity).ToJmaIntensity();
    points = ev.Points.Select(p => new KyoshinEventObservationPoint(p)).ToArray();
    topLeft = ev.TopLeft;
    bottomRight = ev.BottomRight;
  }
  
  public Guid id { get; }
  public KyoshinEventLevel level { get; }
  public DateTime createdAt { get; }

  public int pointCount { get; }

  public JmaIntensity maxIntensity { get; }

  public KyoshinEventObservationPoint[] points { get; }

  public Location topLeft { get; }
  public Location bottomRight { get; }
}


public class KyoshinEventObservationPoint
{
  public KyoshinEventObservationPoint(
    RealtimeObservationPoint point
  )
  {
    location = point.Location;
    intensity = point.LatestIntensity.ToJmaIntensity();
    code = point.Code;
    name = point.Name;
  }

  public Location location { get; }
  public JmaIntensity intensity { get; }
  public string code { get; }
  public string name { get; }

}
