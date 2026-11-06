namespace HCSL.Tui.Presentation;

public sealed record TerminalOptions(bool English)
{
    public string Text(string chinese, string english) => English ? english : chinese;

    public static TerminalOptions Parse(string[] args)
    {
        var english = Environment.GetEnvironmentVariable("TERM") == "linux";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang" when i + 1 < args.Length:
                    english = args[++i] switch
                    {
                        "en" => true, "zh-CN" => false,
                        _ => throw new ArgumentException("Supported languages: en, zh-CN.")
                    };
                    break;
                default: throw new ArgumentException($"Unknown option: {args[i]}. Use --help.");
            }
        }
        return new(english);
    }
}
