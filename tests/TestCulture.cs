using System.Globalization;
using System.Runtime.CompilerServices;

namespace CashGear.Capture.Tests;

/// <summary>
/// Every test assembly starts in English, whatever Windows' display language is: the tests compare the app's text
/// with the English in its .resx files. Tests of the Arabic UI set the culture themselves.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
