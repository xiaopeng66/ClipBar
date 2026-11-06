using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The test project drives a few layout/decision helpers that are internal on purpose (they
// are not part of the app's public surface): the tray menu height bound (F-08) and similar
// seams added together with their regressions.
[assembly: InternalsVisibleTo("ClipBar.App.Tests")]
