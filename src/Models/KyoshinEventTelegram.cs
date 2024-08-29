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
    regions = KyoshinEventObservationRegion.Create(ev.Points.ToArray());
    topLeft = ev.TopLeft;
    bottomRight = ev.BottomRight;
  }

  public Guid id { get; }
  public KyoshinEventLevel level { get; }
  public DateTime createdAt { get; }

  public int pointCount { get; }

  public JmaIntensity maxIntensity { get; }

  public KyoshinEventObservationRegion[] regions { get; }

  public Location topLeft { get; }
  public Location bottomRight { get; }
}



public class KyoshinEventObservationRegion
{

  public static KyoshinEventObservationRegion[] Create(RealtimeObservationPoint[] points)
  {
    var regions = new List<KyoshinEventObservationRegion>();
    // regionでグループ
    var groupedPoints = points.GroupBy(p => p.Region);
    foreach (var group in groupedPoints)
    {
      var region = new KyoshinEventObservationRegion(
        group.Key,
        group.Select(p => new KyoshinEventObservationPoint(p)).ToArray(),
        group.Max(p => p.LatestIntensity.ToJmaIntensity())
      );
      regions.Add(region);
    }
    return regions.ToArray();
  }

  public KyoshinEventObservationRegion(string name, KyoshinEventObservationPoint[] points, JmaIntensity maxIntensity)
  {
    this.name = name;
    this.points = points;
    this.maxIntensity = maxIntensity;
  }

  public JmaIntensity maxIntensity { get; }
  public string name { get; }
  public KyoshinEventObservationPoint[] points { get; }
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
