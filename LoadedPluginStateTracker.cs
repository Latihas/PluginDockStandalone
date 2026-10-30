using System;
using System.Collections.Generic;

namespace PluginDockStandalone;

internal sealed class LoadedPluginStateTracker {
	private const long PollIntervalMilliseconds = 5000;
	private HashSet<string> loadedPluginNames = new(StringComparer.OrdinalIgnoreCase);
	private long nextPollAtTick;

	public void Reset(IEnumerable<string> pluginNames, long nowTick) {
		loadedPluginNames = new HashSet<string>(pluginNames, StringComparer.OrdinalIgnoreCase);
		nextPollAtTick = nowTick + PollIntervalMilliseconds;
	}

	public bool IsPollDue(long nowTick) {
		if (nowTick < nextPollAtTick)
			return false;
		nextPollAtTick = nowTick + PollIntervalMilliseconds;
		return true;
	}

	public bool HasChanged(IEnumerable<string> pluginNames) {
		var currentPluginNames = new HashSet<string>(pluginNames, StringComparer.OrdinalIgnoreCase);
		if (loadedPluginNames.SetEquals(currentPluginNames))
			return false;
		loadedPluginNames = currentPluginNames;
		return true;
	}
}
