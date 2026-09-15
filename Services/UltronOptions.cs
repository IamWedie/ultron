using Microsoft.Extensions.Options;

namespace Ultron.Services;

/// <summary>
/// Central, lazily-created <see cref="IOptions{AppSettings}"/> for the app.
/// Services that used to call <c>AppSettings.Load()</c> (which re-decrypts and
/// re-parse config on every call) resolve config through this single cached
/// instance instead. <see cref="Set"/> is called by the composition root after
/// a settings mutation so consumers observe the new values on next read.
/// </summary>
public static class UltronOptions
{
    private static IOptions<AppSettings>? _instance;

    public static IOptions<AppSettings> Instance => _instance ??= Options.Create(AppSettings.Load());

    public static void Set(AppSettings settings) => _instance = Options.Create(settings);
}