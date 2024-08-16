using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyoshinEewViewer.Core.Models;
using KyoshinEewViewer.Series.KyoshinMonitor.Services;
using KyoshinEewViewer.Services;
using KyoshinMonitorLib;
using KyoshinMonitorLib.SkiaImages;
using KyoshinMonitorLib.UrlGenerator;
using Microsoft.Extensions.Logging;
using Prometheus;
using SkiaSharp;

namespace ShakeDetectService;

public class KyoshinMonitorWatchService
{
  private HttpClient httpClient { get; } = new(new HttpClientHandler()
  {
    AutomaticDecompression = System.Net.DecompressionMethods.All
  })
  {
    Timeout = TimeSpan.FromSeconds(2)
  };

  private ILogger Logger { get; }
  private TimerService TimerService { get; }

  private WebApi webApi { get; }

  public KyoshinMonitorWatchService()
  {
    Logger = (LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("KyoshinMonitorWatchService"));
    TimerService = new TimerService();
    TimerService.TimerElapsed += t =>
    {
      try
      {
        TimerElapsed(t).Wait();
      }
      catch (Exception)
      {
        return;
      }
    };
    webApi = new WebApi();
  }

  private RealtimeObservationPoint[]? Points { get; set; }

  /// <summary>
  /// タイムシフトなども含めた現在時刻
  /// </summary>
  public DateTime CurrentDisplayTime => LastElapsedDelayedTime + (TimerService.CurrentTime - LastElapsedDelayedLocalTime);
  private DateTime LastElapsedDelayedTime { get; set; }
  private DateTime LastElapsedDelayedLocalTime { get; set; }

  public DateTime? OverrideDateTime { get; set; }

  public void Start()
  {
    var sw = Stopwatch.StartNew();
    Logger.LogInformation("Start KyoshinMonitorWatchService");
    Logger.LogInformation("走時表を準備しています");
    TravelTimeTableService.Initialize();
    Logger.LogInformation("走時表の準備が完了しました: {ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);

    sw.Restart();
    Logger.LogInformation("観測点情報を取得しています");
    var baseDirectoryBinIndex = AppContext.BaseDirectory.IndexOf("bin/");
    string observationPointFilePath;
    if (baseDirectoryBinIndex == -1)
    {
      observationPointFilePath = Path.Combine(
        AppContext.BaseDirectory,
      "Resources", "ShindoObsPoints.mpk.lz4"
      );
    }
    else
    {
      observationPointFilePath = Path.Combine(
        AppContext.BaseDirectory.Substring(
          0,
          baseDirectoryBinIndex
        ),
      "Resources", "ShindoObsPoints.mpk.lz4"
      );
    }
    using (var observationPointStream = File.OpenRead(observationPointFilePath) ?? throw new FileNotFoundException("観測点情報ファイルが見つかりません")
    )
    {
      var points = ObservationPoint.LoadFromMpk(observationPointStream, true);
      Points = points.Where(p => p.Point != null && !p.IsSuspended).Select(p => new RealtimeObservationPoint(p)).ToArray();
    }
    Logger.LogInformation("観測点情報の取得が完了しました: {ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);

    foreach (var point in Points)
      // 60キロ以内の近い順の最大15観測点を関連付ける
      // 生活振動が多い神奈川･東京は20観測点とする
      point.NearPoints = Points
        .Where(p => point != p && point.Location.Distance(p.Location) < 60)
        .OrderBy(p => point.Location.Distance(p.Location))
        .Take(point.Region is "神奈川県" or "東京都" ? 20 : 15)
        .ToArray();

    TimerService.StartMainTimer();
  }


  private bool IsRunning { get; set; }
  private double Offset { get; set; } = 1000;

  private static readonly Histogram kyoshinMonitorFetchDuration = Metrics.CreateHistogram("kyoshin_monitor_fetch_duration_seconds", "The duration of fetching KyoshinMonitor data");
  private static readonly Histogram kyoshinMonitorProcessDuration = Metrics.CreateHistogram("kyoshin_monitor_process_duration_seconds", "The duration of processing KyoshinMonitor data");

  private static readonly Counter kyoshinMonitorFetchError = Metrics.CreateCounter("kyoshin_monitor_fetch_error", "The count of KyoshinMonitor fetch error");
  private static readonly Counter kyoshinMonitorFetchSuccess = Metrics.CreateCounter("kyoshin_monitor_fetch_success", "The count of KyoshinMonitor fetch success");
  private static readonly Counter kyoshinMonitorShakeEvent = Metrics.CreateCounter("kyoshin_monitor_shake_event", "The count of KyoshinMonitor shake event");

  private async Task TimerElapsed(DateTime realTime)
  {
    // 観測点が読み込みできていなければ処理しない
    if (Points == null)
      return;

    var time = realTime.AddMilliseconds(Offset * -1);
    // 時刻が変化したときのみ
    if (LastElapsedDelayedTime != time)
      LastElapsedDelayedLocalTime = TimerService.CurrentTime;
    LastElapsedDelayedTime = time;


    // すでに処理中であれば戻る
    if (IsRunning)
      return;
    IsRunning = true;

    await kyoshinMonitorFetchError.CountExceptions(
     async () =>
     {
       try
       {
         HttpResponseMessage response;
         using (kyoshinMonitorFetchDuration.NewTimer())
         {
           // 画像をGET
           var url = WebApiUrlGenerator.Generate(WebApiUrlType.RealtimeImg, time, RealtimeDataType.Shindo, false);
           response = await httpClient.GetAsync(url);
         }
         kyoshinMonitorFetchSuccess.Inc();
         if (response.StatusCode != HttpStatusCode.OK)
         {
           Offset = Math.Min(5000, Offset + 100);
           Logger.LogInformation($"{time:HH:mm:ss} オフセットを調整してください。: {Offset}ms");
           return;
         }
         // オフセットが大きい場合1分に1回短縮を試みる
         if (time.Second == 0 && Offset > 1100)
           Offset -= 100;

         //画像から取得
         var bitmap = SKBitmap.Decode(await response.Content.ReadAsStreamAsync());
         if (bitmap != null)
           using (kyoshinMonitorProcessDuration.NewTimer())
           {
             using (bitmap)
             {
               ProcessImage(bitmap, time);
             }
           }

       }
       catch (AggregateException ex)
       {
         Logger.LogWarning(ex, "取得に失敗しました。");
         throw;
       }
       catch (TaskCanceledException ex)
       {
         Logger.LogWarning(ex, "取得にタイムアウトしました。");
         throw;
       }
       catch (KyoshinMonitorException ex)
       {
         Logger.LogWarning(ex, "取得にタイムアウトしました。");
         throw;
       }
       catch (HttpRequestException ex)
       {
         Logger.LogWarning(ex, "HTTPエラー");
         throw;
       }
       catch (Exception ex)
       {
         Logger.LogWarning(ex, "汎用エラー");
         throw;
       }
       finally
       {
         IsRunning = false;
       }
     }
    );
  }

  private List<KyoshinEvent> KyoshinEvents { get; } = [];
  private void ProcessImage(SKBitmap bitmap, DateTime time)
  {
    if (Points == null)
      return;

    // パース
    foreach (var point in Points)
    {
      var color = bitmap.GetPixel(point.ImageLocation.X, point.ImageLocation.Y);
      if (color.Alpha != 255)
      {
        point.Update(null, null);
        continue;
      }
      var intensity = ColorConverter.ConvertToIntensityFromScale(ColorConverter.ConvertToScaleAtPolynomialInterpolation(color));
      point.Update(color, (float)intensity);
    }

    // イベントチェック･異常値除外
    foreach (var point in Points)
    {
      // 異常値の排除
      if (point.LatestIntensity is { } latestIntensity &&
        point.IntensityDiff < 1 && point.Event == null &&
        latestIntensity >= (point.HasNearPoints ? 3 : 5) && // 震度3以上 離島は5以上
        Math.Abs(point.IntensityAverage - latestIntensity) <= 1 && // 10秒間平均で 1.0 の範囲
        (
          point.IsTmpDisabled || (point.NearPoints?.All(p => (latestIntensity - p.LatestIntensity ?? -3) >= 3) ?? true)
        ))
      {
        if (!point.IsTmpDisabled)
          Logger.LogInformation($"異常値の判定により観測点の除外を行いました: {point.Code} {point.LatestIntensity} {point.IntensityAverage}");
        point.IsTmpDisabled = true;
      }
      else if (point.LatestIntensity != null && point.IsTmpDisabled)
      {
        Logger.LogInformation($"異常値による除外を戻します: {point.Code} {point.LatestIntensity} {point.IntensityAverage}");
        point.IsTmpDisabled = false;
      }

      // 除外されている観測点はイベントの検出に使用しない
      if (point.IsTmpDisabled)
        continue;

      if (point.IntensityDiff < 1.1)
      {
        // 未来もしくは過去のイベントは離脱
        if (point.Event is { } evt && (point.EventedAt > time || point.EventedExpireAt < time))
        {
          Logger.LogDebug($"揺れ検知終了: {point.Code} {evt.Id} {time} {point.EventedAt} {point.EventedExpireAt}");
          point.Event = null;
          evt.RemovePoint(point);

          if (evt.PointCount <= 0)
          {
            KyoshinEvents.Remove(evt);
            Logger.LogDebug($"イベント終了: {evt.Id}");
          }
        }
        continue;
      }
      // 周囲の観測点が未計算の場合もしくは欠測の場合戻る
      if (point.NearPoints == null || point.LatestIntensity == null)
        continue;

      // 有効な周囲の観測点の数
      var availableNearCount = point.NearPoints.Count(n => n.HasValidHistory);

      // 周囲の観測点が存在しない場合 2 以上でeventedとしてマーク
      if (availableNearCount == 0)
      {
        if (point.IntensityDiff >= 2 && point.Event == null)
        {
          point.Event = new(time, point);
          point.EventedAt = time;
          KyoshinEvents.Add(point.Event);
          Logger.LogDebug($"揺れ検知(単独): {point.Code} 変位: {point.IntensityDiff} {point.Event.Id}");
        }
        continue;
      }

      var events = new List<KyoshinEvent>();
      if (point.Event != null)
        events.Add(point.Event);
      var count = 0;
      // 周囲の観測点の 1/3 以上 0.5 であればEventedとしてマーク
      var threshold = Math.Min(availableNearCount, Math.Max(availableNearCount / 3, 4));
      // 東京･神奈川の場合はちょっと閾値を高くする
      if (point.Region is "東京都" or "神奈川県")
        threshold = Math.Min(availableNearCount, (int)Math.Max(availableNearCount / 1.5, 4));

      foreach (var np in point.NearPoints)
      {
        if (!np.IsTmpDisabled && np.IntensityDiff >= 0.5)
        {
          count++;
          if (np.Event != null)
            events.Add(np.Event);
        }
      }
      if (count < threshold)
        continue;

      // この時点で検知扱い
      point.EventedAt = time;

      var uniqueEvents = events.Distinct();
      // 複数件ある場合イベントをマージする
      if (uniqueEvents.Count() > 1)
      {
        // createdAt が一番古いイベントにマージする
        var firstEvent = uniqueEvents.OrderBy(e => e.CreatedAt).First();
        foreach (var evt in uniqueEvents)
        {
          if (evt == firstEvent)
            continue;
          firstEvent.MergeEvent(evt);
          KyoshinEvents.Remove(evt);
          Logger.LogDebug($"イベント統合: {firstEvent.Id} <- {evt.Id}");
        }

        // マージしたイベントと異なる状態だった場合追加
        if (point.Event == firstEvent)
          continue;
        if (point.Event == null)
          Logger.LogDebug($"揺れ検知: {point.Code} {firstEvent.Id} 利用数:{count} 閾値:{threshold} 総数:{point.NearPoints.Length}");
        firstEvent.AddPoint(point, time);
        continue;
      }
      // 1件の場合はイベントに追加
      if (uniqueEvents.Any())
      {
        if (point.Event == null)
          Logger.LogDebug($"揺れ検知: {point.Code} {events[0].Id} 利用数:{count} 閾値:{threshold} 総数:{point.NearPoints.Length}");
        events[0].AddPoint(point, time);
        continue;
      }

      // 存在しなかった場合はイベント作成
      if (point.Event == null)
      {
        point.Event = new(time, point);
        KyoshinEvents.Add(point.Event);
        Logger.LogDebug($"揺れ検知(新規): {point.Code} {point.Event.Id} 利用数:{count} 閾値:{threshold} 総数:{point.NearPoints.Length}");
      }
    }

    // イベントの紐づけ
    foreach (var evt in KyoshinEvents.OrderBy(e => e.CreatedAt).ToArray())
    {
      if (!KyoshinEvents.Contains(evt))
        continue;

      // 2つのイベントが 一定距離未満の場合マージする
      foreach (var evt2 in KyoshinEvents.Where(e => e != evt && evt.CheckNearby(e)).ToArray())
      {
        evt.MergeEvent(evt2);
        KyoshinEvents.Remove(evt2);
        Logger.LogDebug($"イベント距離統合: {evt.Id} <- {evt2.Id}");
      }
    }

    // 出力
    foreach (var evt in KyoshinEvents)
    {
      Logger.LogWarning($"イベント: {evt.Id} {evt.CreatedAt} {evt.PointCount} {evt.Points.Select(p => p.Code).Aggregate((p, n) => $"{p},{n}")}");
    }

    kyoshinMonitorShakeEvent.IncTo(KyoshinEvents.Count);

    // ファイル出力
    if (KyoshinEvents.Any())
    {
      var baseDirectoryBinIndex = AppContext.BaseDirectory.IndexOf("bin/");
      string path;
      if (baseDirectoryBinIndex == -1)
      {
        path = Path.Combine(
          AppContext.BaseDirectory,
        "data", "events", $"{time:yyyyMMddHHmmss}.json"
        );
      }
      else
      {
        path = Path.Combine(
          AppContext.BaseDirectory.Substring(
            0,
            baseDirectoryBinIndex
          ),
        "data", "events", $"{time:yyyyMMddHHmmss}.json"
        );
      }
      var dir = Path.GetDirectoryName(path);
      if (dir == null)
        return;
      if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);

      Directory.CreateDirectory(dir);
      File.WriteAllText(path, JsonSerializer.Serialize(KyoshinEvents, new JsonSerializerOptions()
      {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.Preserve
      }));
    }
  }

}
