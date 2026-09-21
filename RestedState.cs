using System;
using System.Collections.Generic;
using System.Globalization;

namespace ServerCharacters;

internal static class RestedState
{
	internal const string Key = "ServerCharacters RestedRemaining";
	internal static bool Valid(float seconds) => seconds > 0 && !float.IsNaN(seconds) && !float.IsInfinity(seconds);

	internal static void Store(Dictionary<string, string> data, float remaining, bool dead)
	{
		data.Remove(Key);
		if (!dead && Valid(remaining)) data[Key] = remaining.ToString("R", CultureInfo.InvariantCulture);
	}

	internal static bool Take(Dictionary<string, string> data, bool dead, out float remaining)
	{
		remaining = 0;
		if (!data.TryGetValue(Key, out string value)) return false;
		data.Remove(Key); // Consume once; never reuse a stale duration after death or expiry.
		return !dead && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out remaining) && Valid(remaining);
	}
}
