namespace HorizonRadio.Sdk;

/// <summary>Where plugins keep their own settings (<c>plugin.&lt;id&gt;.&lt;key&gt;</c> in the app's settings).</summary>
public interface IPluginSettingsStore
{
    /// <summary>
    /// The settings belonging to the plugin with id <paramref name="pluginId"/> (pass your own
    /// <see cref="IOrdered.Id"/>). They stay on this PC, except the keys <paramref name="synced"/>
    /// says yes to, which follow the user to their other PCs once they've paired them: preferences,
    /// not paths or facts about this machine. Watch <see cref="IPluginSettings.Changed"/> to follow
    /// a change made elsewhere.
    /// </summary>
    IPluginSettings ForPlugin(string pluginId, Func<string, bool>? synced = null);
}
