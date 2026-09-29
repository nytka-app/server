namespace Nytka.Server.Tests.Search;

/// <summary>
/// A test that needs the Ukrainian dictionary files in <c>tsearch_data/</c> (scripts/fetch-uk-dictionary.sh). It is
/// skipped with a message when they are missing, unless <c>NYTKA_REQUIRE_DICTIONARY</c> is set, as CI sets it,
/// so a broken fetch fails the build instead of skipping the tests.
/// </summary>
public sealed class DictionaryFactAttribute : FactAttribute
{
    public DictionaryFactAttribute()
    {
        if (!Dictionary.Available && Environment.GetEnvironmentVariable("NYTKA_REQUIRE_DICTIONARY") is null)
        {
            Skip = "tsearch_data/ has no dictionary; run scripts/fetch-uk-dictionary.sh.";
        }
    }
}

public static class Dictionary
{
    public static readonly string Directory = FindDirectory();

    public static string Dict => Path.Combine(Directory, "uk_ua.dict");

    public static string Affix => Path.Combine(Directory, "uk_ua.affix");

    public static bool Available => File.Exists(Dict) && File.Exists(Affix);

    private static string FindDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            {
                return Path.Combine(dir.FullName, "tsearch_data");
            }
        }

        throw new InvalidOperationException("The repository root (db/migrations) was not found above the test binaries.");
    }
}
