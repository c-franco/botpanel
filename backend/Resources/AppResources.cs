using System.Globalization;
using System.Resources;

namespace BotPanel.Resources;

internal static class AppResources
{
    private static readonly ResourceManager ResourceManager =
        new("BotPanel.Resources.AppResources", typeof(AppResources).Assembly);

    public static string BotAlreadyExists => GetString(nameof(BotAlreadyExists));
    public static string BotAlreadyRunning => GetString(nameof(BotAlreadyRunning));
    public static string BotNameRequired => GetString(nameof(BotNameRequired));
    public static string BotNotFound => GetString(nameof(BotNotFound));
    public static string BotNotRunning => GetString(nameof(BotNotRunning));
    public static string ContainerRemoved => GetString(nameof(ContainerRemoved));
    public static string ContainerStarted => GetString(nameof(ContainerStarted));
    public static string DisconnectedClient => GetString(nameof(DisconnectedClient));
    public static string DockerLogsStartFailed => GetString(nameof(DockerLogsStartFailed));
    public static string DockerProcessStartFailed => GetString(nameof(DockerProcessStartFailed));
    public static string DockerStartFailed => GetString(nameof(DockerStartFailed));
    public static string EditableFilesOnly => GetString(nameof(EditableFilesOnly));
    public static string FailedToGetLogs => GetString(nameof(FailedToGetLogs));
    public static string FailedToStartBot => GetString(nameof(FailedToStartBot));
    public static string FailedToStopBot => GetString(nameof(FailedToStopBot));
    public static string LoadedBot => GetString(nameof(LoadedBot));
    public static string LogStreamCancelled => GetString(nameof(LogStreamCancelled));
    public static string LogStreamError => GetString(nameof(LogStreamError));
    public static string LogStreamStarting => GetString(nameof(LogStreamStarting));
    public static string RecoveredBot => GetString(nameof(RecoveredBot));
    public static string StartedBot => GetString(nameof(StartedBot));
    public static string StartingContainer => GetString(nameof(StartingContainer));
    public static string StoppedBot => GetString(nameof(StoppedBot));
    public static string StoppingContainer => GetString(nameof(StoppingContainer));
    public static string SubscribedClient => GetString(nameof(SubscribedClient));

    public static string Format(string resource, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, resource, args);

    private static string GetString(string name) =>
        ResourceManager.GetString(name, CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"Missing resource '{name}'.");
}
