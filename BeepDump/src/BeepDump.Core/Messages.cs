namespace BeepDump.Core;

/// <summary>表示する文の言語。日本語と英語を並べて書き、使う方を選ぶ。</summary>
public static class Messages
{
    public static bool English { get; set; }

    public static string Pick(string japanese, string english) => English ? english : japanese;
}
