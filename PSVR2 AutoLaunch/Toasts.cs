using Microsoft.Toolkit.Uwp.Notifications;

namespace PSVR2_AutoLaunch
{
    // Windows toast notifications (Action Center). Unlike NotifyIcon balloon tips -
    // which share a single slot and stomp on each other - toasts are addressed by
    // tag+group: re-showing with the same tag replaces that toast in place, so each
    // controller side gets its own independently-updating notification.
    internal static class Toasts
    {
        private const string ToastGroup = "PSVR2AutoLaunch";

        public static void Show(string tag, string title, string body)
        {
            var builder = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrEmpty(body))
                builder.AddText(body);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = ToastGroup;
            });
        }

        // Pull a previously shown toast back out of Action Center. Used when a flow
        // resolves benignly: rather than adding an "all clear" notification, the
        // prompt that asked for action simply disappears.
        public static void Remove(string tag)
        {
            ToastNotificationManagerCompat.History.Remove(tag, ToastGroup);
        }
    }
}
