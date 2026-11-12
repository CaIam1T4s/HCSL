using System.Reflection;

namespace HCSL.Host.Services;

/// <summary>当前应用版本，取自项目文件生成的程序集信息。</summary>
public static class ProductVersion
{
    public static string Current { get; } = Read();

    public static string Display => $"HCSL v{Current}";

    private static string Read()
    {
        var informational = typeof(ProductVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉源链接追加的 "+<commit>" 构建元数据，仅版本号不重复。
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = typeof(ProductVersion).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
