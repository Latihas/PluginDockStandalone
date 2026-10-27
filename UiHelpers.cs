using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;

namespace PluginDockStandalone;

internal static class ImGuiOm {
	public static void HelpMarker(string text) {
		ImGui.TextDisabled("(?)");
		if (!ImGui.IsItemHovered())
			return;

		ImGui.BeginTooltip();
		ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
		ImGui.TextUnformatted(text);
		ImGui.PopTextWrapPos();
		ImGui.EndTooltip();
	}
}

internal static class HelpersOm {
	public static void NotificationInfo(string message) {
		DService.NotificationManager.AddNotification(new Notification {
			Type = NotificationType.Info,
			Content = message
		});
	}

	public static void NotificationError(string message) {
		DService.NotificationManager.AddNotification(new Notification {
			Type = NotificationType.Error,
			Content = message
		});
	}
}