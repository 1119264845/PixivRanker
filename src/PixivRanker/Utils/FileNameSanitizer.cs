using System.IO;
using System.Text;

namespace PixivRanker.Utils;

public static class FileNameSanitizer
{
    private static readonly HashSet<char> InvalidCharacters =
        Path.GetInvalidFileNameChars().ToHashSet();

    public static string Sanitize(string value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未命名";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(InvalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        var result = builder.ToString().TrimEnd('.', ' ');
        return result.Length <= maxLength ? result : result[..maxLength].TrimEnd('.', ' ');
    }
}
