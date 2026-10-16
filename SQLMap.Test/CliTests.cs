using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace SQLMaps.Test;

/// <summary>
///     Runs the built SQLECmd.dll (copied next to the tests by the project reference) as a separate process, from an
///     empty working directory. It uses the Maps folder next to it, which has the TestFiles_*.smap maps
/// </summary>
[TestFixture]
public class CliTests
{
    private const string ContactsId = "83b99299-2d84-4844-af25-c727d3440b19";
    private const string CarsId = "af5bcf3f-b316-4e49-8d12-ce9e180b3914";

    private string _dir;
    private string _cwd;
    private string Csv => Path.Combine(_dir, "csv");
    private string Json => Path.Combine(_dir, "json");

    private static string[] InvalidJsonFileNames => new[]
    {
        "", "   ", ".", "..", "nested" + Path.DirectorySeparatorChar
    };

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlecmd-test-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dir, "cwd");
        Directory.CreateDirectory(_cwd);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, true);
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _cwd
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "SQLECmd.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        process.WaitForExit(120_000).Should().BeTrue("SQLECmd should finish");

        // SQLECmd must not leave anything (such as SQLite.Interop.dll) in the working directory
        Directory.GetFileSystemEntries(_cwd).Should().BeEmpty();

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string[] CsvLines(string suffix)
    {
        var file = Directory.GetFiles(Csv, "*" + suffix).Should().ContainSingle().Subject;
        return File.ReadAllLines(file);
    }

    private static void AssertContactsJson(string file)
    {
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(20 + 20 + 36 + 20);
        lines[0].Should().StartWith("{\"FirstName\":\"John\",\"LastName\":\"Doe\",\"SourceFile\":")
            .And.EndWith(",\"MapDescription\":\"Some rando contacts database example\"}");
    }

    [Test]
    public void ContactsByFileName()
    {
        var db = SqLiteFileTests.TestFile("Contacts.db");

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);
        output.Should().Contain("Map queries found: 4");

        var users = CsvLines($"_Contacts_Users_{ContactsId}.csv");
        users.Should().HaveCount(21);
        users[0].Should().Be("FirstName,LastName,SourceFile");
        users[1].Should().Be($"John,Doe,{db}");

        var join = CsvLines($"_Contacts_JoinExample_{ContactsId}.csv");
        join.Should().HaveCount(37);
        join[0].Should().StartWith("ID,CustomerName,Year,January,");

        CsvLines($"_Contacts_StateIdInfo_{ContactsId}.csv")[0].Should().Be("ID,Wizzo,SourceFile");
    }

    [Test]
    public void HuntFindsEveryTestDatabase()
    {
        var (exitCode, output) = Run("-d", Path.Combine(TestContext.CurrentContext.TestDirectory, "TestFiles"),
            "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("Processed 5 files");
        output.Should().NotContain("Error processing");
        // other maps not matching is normal when hunting, so it is only shown with --debug
        output.Should().NotContain("Queries will not be processed");

        CsvLines($"_Cars_Companies_{CarsId}.csv").Should().HaveCount(20);
        CsvLines($"_Cars_PaymentTypeAndAmounts_{CarsId}.csv").Should().HaveCount(1001);
        CsvLines($"_Contacts_Users_{ContactsId}.csv").Should().HaveCount(21);
    }

    [Test]
    public void BlobsAreExtracted()
    {
        var db = SqLiteFileTests.TestFile("BlobTest_Three.db");

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);

        // Data: named from the Name column, .txt; DataTwo: .blob, only where it is not NULL
        var blobs = Directory.GetFiles(Csv).Where(t => !t.EndsWith(".csv")).Select(Path.GetFileName).ToList();
        blobs.Should().Contain(t => t.EndsWith("_ValidName1_Data.txt"));
        blobs.Should().Contain(t => t.EndsWith("_ValidName1_DataTwo.blob"));
        blobs.Should().Contain(t => t.EndsWith("_File_Name_With_Spaces_Slashes_Data.txt"));
        blobs.Should().OnlyContain(t => t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

        var csv = Directory.GetFiles(Csv, "*.csv").Should().ContainSingle().Subject;
        File.ReadAllLines(csv)[0].Should().Be("Id,Name,SourceFile", "blob columns are not written to the CSV");
    }

    [Test]
    public void NoBlobSkipsExtraction()
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("BlobTest_Three.db"), "--csv", Csv, "--hunt",
            "false", "--noblob");

        exitCode.Should().Be(0, output);
        Directory.GetFiles(Csv).Should().ContainSingle().Which.Should().EndWith(".csv");
    }

    [Test]
    public void JsonHasOneLinePerRow()
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--json",
            Json, "--hunt", "false");

        exitCode.Should().Be(0, output);

        var json = Directory.GetFiles(Json, "*_SQLECmd_Output.json").Should().ContainSingle()
            .Subject;
        AssertContactsJson(json);
    }

    [Test]
    public void HelpDocumentsJsonFileNameOverride()
    {
        var (exitCode, output) = Run("--help");

        exitCode.Should().Be(0, output);
        var help = Regex.Replace(output, @"\s+", " ");
        help.Should().Contain("--jsonf <jsonf>");
        help.Should().Contain("File name to save JSON formatted results to. When present, overrides default name");
    }

    [TestCase("custom results.json")]
    [TestCase("custom-results.jsonl")]
    [TestCase("custom-results")]
    public void JsonFileNameOverridesDefaultExactly(string name)
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--json", Json,
            "--jsonf", name, "--hunt", "false");

        exitCode.Should().Be(0, output);
        Directory.GetFiles(Json).Should().ContainSingle().Which.Should().Be(Path.Combine(Json, name));
        AssertContactsJson(Path.Combine(Json, name));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void JsonFileNameUsesOnlyTheBaseName(bool absolute)
    {
        var supplied = absolute
            ? Path.Combine(_dir, "nested", "custom.json")
            : Path.Combine("..", "nested", "custom.json");

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--json", Json,
            "--jsonf", supplied, "--hunt", "false");

        exitCode.Should().Be(0, output);
        Directory.GetFiles(Json).Should().ContainSingle().Which.Should().Be(Path.Combine(Json, "custom.json"));
        AssertContactsJson(Path.Combine(Json, "custom.json"));
        Directory.Exists(Path.Combine(_dir, "nested")).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void JsonFileNameRequiresJsonDirectoryBeforeAnyExports(bool withCsv)
    {
        var args = new[] { "-f", SqLiteFileTests.TestFile("Contacts.db"), "--jsonf", "custom.json" };
        if (withCsv)
        {
            args = args.Concat(new[] { "--csv", Csv }).ToArray();
        }

        var (exitCode, output) = Run(args);

        exitCode.Should().Be(1, output);
        output.Should().Contain("--jsonf requires --json");
        Directory.Exists(Csv).Should().BeFalse();
        Directory.Exists(Json).Should().BeFalse();
    }

    [TestCaseSource(nameof(InvalidJsonFileNames))]
    public void InvalidJsonFileNameFailsBeforeAnyExports(string name)
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv,
            "--json", Json, "--jsonf", name);

        exitCode.Should().Be(1, output);
        output.Should().Contain("--jsonf").And.Contain("file name");
        Directory.Exists(Csv).Should().BeFalse();
        Directory.Exists(Json).Should().BeFalse();
    }

    [Test]
    public void ExistingJsonDestinationFailsWithoutChangingItsContents()
    {
        Directory.CreateDirectory(Json);
        var destination = Path.Combine(Json, "custom.json");
        var original = new byte[] { 0, 1, 2, 0xFF };
        File.WriteAllBytes(destination, original);

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--json", Json,
            "--jsonf", "custom.json");

        exitCode.Should().Be(1, output);
        output.Should().Contain("Unable to create JSON output");
        File.ReadAllBytes(destination).Should().Equal(original);
        Directory.GetFiles(Json).Should().ContainSingle().Which.Should().Be(destination);
    }

    [Test]
    public void JsonDestinationCannotOverwriteTheInputDatabase()
    {
        var input = Path.Combine(_dir, "Contacts.db");
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), input);
        var original = File.ReadAllBytes(input);

        var (exitCode, output) = Run("-f", input, "--json", _dir, "--jsonf", "Contacts.db");

        exitCode.Should().Be(1, output);
        output.Should().Contain("Unable to create JSON output");
        File.ReadAllBytes(input).Should().Equal(original);
    }

    [Test]
    public void DirectoryInputUsesOneNamedJsonFileAlongsideCsv()
    {
        var input = Path.Combine(_dir, "in");
        var first = Path.Combine(input, "a", "Contacts.db");
        var second = Path.Combine(input, "b", "Contacts.db");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), first);
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), second);

        var (exitCode, output) = Run("-d", input, "--csv", Csv, "--json", Json, "--jsonf", "all.jsonl",
            "--dedupe", "false", "--hunt", "true");

        exitCode.Should().Be(0, output);
        var destination = Directory.GetFiles(Json).Should().ContainSingle().Subject;
        destination.Should().Be(Path.Combine(Json, "all.jsonl"));
        var lines = File.ReadAllLines(destination);
        lines.Should().HaveCount(2 * (20 + 20 + 36 + 20));
        var sources = lines.Select(line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("SourceFile").GetString();
        }).ToArray();
        sources.Should().OnlyContain(source => source == first || source == second);
        sources.Count(source => source == first).Should().Be(96);
        sources.Count(source => source == second).Should().Be(96);
        Directory.GetFiles(Csv, "*.csv").Should().HaveCount(8);
    }

    [Test]
    public void DuplicatesAreSkipped()
    {
        var input = Path.Combine(_dir, "in");
        Directory.CreateDirectory(Path.Combine(input, "a"));
        Directory.CreateDirectory(Path.Combine(input, "b"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(input, "a", "Contacts.db"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(input, "b", "Contacts.db"));

        var (exitCode, output) = Run("-d", input, "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("has already been processed");
        output.Should().Contain("Processed 1 file in");
    }

    [Test]
    public void UnmatchedDatabaseIsReported()
    {
        var input = Path.Combine(_dir, "in");
        Directory.CreateDirectory(input);
        var db = Path.Combine(input, "other.db");
        File.Copy(SqLiteFileTests.TestFile("BlobTest.db"), db);

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);
        output.Should().Contain("At least one database was found with no corresponding map");
        output.Should().Contain("Tables: TestBlobs,sqlite_sequence");
    }

    [Test]
    public void MissingArgumentsFail()
    {
        Run().ExitCode.Should().Be(1);
        Run("-f", SqLiteFileTests.TestFile("Contacts.db")).ExitCode.Should().Be(1, "--csv or --json is required");
        Run("-f", Path.Combine(_dir, "missing.db"), "--csv", Csv).ExitCode.Should().Be(1);
    }

    [Test]
    public void BadMapFails()
    {
        var maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllText(Path.Combine(maps, "Bad.smap"), "Description: no queries\nAuthor: a\n");

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--maps", maps);

        exitCode.Should().Be(1);
        output.Should().Contain("Bad.smap");
    }

    [Test]
    public void CorruptDatabaseFails()
    {
        var db = Path.Combine(_dir, "Contacts.db");
        var bytes = File.ReadAllBytes(SqLiteFileTests.TestFile("Contacts.db"));
        // keep the header so it is still found, then garbage
        for (var i = 100; i < bytes.Length; i++)
        {
            bytes[i] = 0xAA;
        }

        File.WriteAllBytes(db, bytes);

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(1, output);
        output.Should().Contain("Unable to read database: database disk image is malformed");
        output.Should().Contain("1 file(s) could not be processed");
        output.Should().NotContain("No maps found");
    }
}
