using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hitbloq.Utilities;

namespace Hitbloq.Other
{
	internal static class PoolOptionPreparation
	{
		internal sealed class SortRequest
		{
			private readonly KeyValuePair<string, float>[] _pools;
			private readonly KeyValuePair<string, int>[]? _popularities;

			public SortRequest(KeyValuePair<string, float>[] pools, KeyValuePair<string, int>[]? popularities)
			{
				_pools = pools;
				_popularities = popularities;
			}

			public static KeyValuePair<string, float>[] Process(object state)
			{
				var request = (SortRequest) state;
				if (request._popularities == null)
				{
					return request._pools;
				}
				var popularityByPool = request._popularities.ToDictionary(pool => pool.Key, pool => pool.Value);
				return request._pools
					.OrderByDescending(pool => popularityByPool.TryGetValue(pool.Key, out var popularity) ? popularity : int.MinValue)
					.ThenBy(pool => pool.Key, StringComparer.Ordinal)
					.ToArray();
			}
		}

		internal sealed class LabelRequest
		{
			private readonly string _id;
			private readonly float _rating;
			private readonly string? _shownName;
			private readonly bool _hasInfo;
			private readonly NumberFormatInfo? _numberFormat;

			public LabelRequest(string id, float rating, string? shownName, bool hasInfo, NumberFormatInfo? numberFormat)
			{
				_id = id;
				_rating = rating;
				_shownName = shownName;
				_hasInfo = hasInfo;
				_numberFormat = numberFormat;
			}

			public static string Process(object state)
			{
				var request = (LabelRequest) state;
				var name = request._hasInfo ? request._shownName!.RemoveSpecialCharacters() : request._id;
				if (name.DoesNotHaveAlphaNumericCharacters())
				{
					name = request._id;
				}
				if (name.Length > 18)
				{
					name = $"{name.Substring(0, 15)}...";
				}
				return name + " - " + request._rating.ToString(null, request._numberFormat) + "⭐";
			}
		}
	}
}
