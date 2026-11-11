namespace Pulsar;

static class Russian
{
    public static string Files(int count)
    {
        int last = Math.Abs(count % 10), pair = Math.Abs(count % 100);
        string word = pair is >= 11 and <= 14 ? "файлов" : last == 1 ? "файл" : last is >= 2 and <= 4 ? "файла" : "файлов";
        return $"{count} {word}";
    }
}
