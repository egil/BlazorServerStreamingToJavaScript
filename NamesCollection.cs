using System.Collections;

namespace BlazorServerStreamingToJavaScript;

public class NamesCollection : IReadOnlyList<string>
{
    private static Bogus.Faker Faker = new();
    private static List<string> Data = Enumerable
        .Range(0, 1_000_000)
        .Select(x => Faker.Name.FullName())
        .ToList();

    public string this[int index] => Data[index];

    public int Count => Data.Count;

    public IEnumerator<string> GetEnumerator() => Data.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
