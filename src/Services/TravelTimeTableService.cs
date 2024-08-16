using MessagePack;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Reflection;

namespace KyoshinEewViewer.Series.KyoshinMonitor.Services;

public static class TravelTimeTableService
{
	private static TravelTimeTableItem[]? TimeTable { get; set; }

	public static (double? pDistance, double? sDistance) CalcDistance(DateTime occurranceTime, DateTime currentTime, int depth)
	{
		if (TimeTable == null)
			throw new InvalidOperationException("走時表の初期化が行われていません");
		if (!TimeTable.Any(t => t.Distance == depth))
			return (null, null);
		var elapsedTime = (currentTime - occurranceTime).TotalMilliseconds;
		if (elapsedTime <= 0)
			return (null, null);

		double? pDistance = null;
		double? sDistance = null;

		TravelTimeTableItem? lastItem = null;
		foreach (var item in TimeTable) // P
		{
			if (item.Depth != depth)
				continue;
			if (item.PTime > elapsedTime)
			{
				if (lastItem == null)
					break;
				// 時間での割合を計算
				var magn = (elapsedTime - lastItem.PTime) / (item.PTime - lastItem.PTime);
				pDistance = magn * (item.Distance - lastItem.Distance) + lastItem.Distance;
				break;
			}
			lastItem = item;
		}
		foreach (var item in TimeTable) // S
		{
			if (item.Depth != depth)
				continue;
			if (item.STime > elapsedTime)
			{
				if (lastItem == null)
					break;
				// 時間での割合を計算
				var magn = (elapsedTime - lastItem.STime) / (item.STime - lastItem.STime);
				sDistance = magn * (item.Distance - lastItem.Distance) + lastItem.Distance;
				break;
			}
			lastItem = item;
		}
		return (pDistance, sDistance);
	}

	public static void Initialize()
	{
		// println
		var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("TravelTimeTableService");
		logger.LogInformation("走時表を準備しています");
		var baseDirectoryBinIndex = AppContext.BaseDirectory.IndexOf("bin/");
		string path;
		if (baseDirectoryBinIndex == -1)
		{
			path = Path.Combine(
				AppContext.BaseDirectory,
				"Resources", "tjma2001.mpk.lz4"
			);
		}
		else
		{
			path = Path.Combine(
				AppContext.BaseDirectory.Substring(
					0,
					baseDirectoryBinIndex
				),
				"Resources", "tjma2001.mpk.lz4"
			);
		}
		logger.LogInformation(path);
		var stream = File.OpenRead(path) ?? throw new FileNotFoundException("走時表ファイルが見つかりません");

		TimeTable = MessagePackSerializer.Deserialize<ImmutableArray<TravelTimeTableItem>>(
			stream,
			MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray)
		).ToArray();
	}
}

[MessagePackObject]
public class TravelTimeTableItem(int pTime, int sTime, int depth, int distance)
{
	[Key(0)]
	public int PTime { get; } = pTime;

	[Key(1)]
	public int STime { get; } = sTime;

	[Key(2)]
	public int Depth { get; } = depth;

	[Key(3)]
	public int Distance { get; } = distance;
}
