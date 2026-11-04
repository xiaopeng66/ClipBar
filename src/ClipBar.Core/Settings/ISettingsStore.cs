using ClipBar.Core.Models;

namespace ClipBar.Core.Settings;

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
