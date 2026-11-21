using System;
using System.Globalization;
using System.Resources;

namespace Chomper.Core.Localization;

internal static class GameText
{
    private static readonly ResourceManager Manager =
        new("Chomper.Core.Localization.Resources", typeof(GameText).Assembly);

    /// <summary>Reads one UI string from the RESX resource manager.</summary>
    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Formats one localized UI string using the active culture.</summary>
    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}
