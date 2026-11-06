namespace HCSL.Host.Services;

public enum ThemePreference { System, Light, Dark }

public interface IThemeService
{
    void Apply(ThemePreference preference);
}
