using System.Text;
using AdoNetCore.AseClient.Interface;
using AdoNetCore.AseClient.Internal;
using NUnit.Framework;

namespace AdoNetCore.AseClient.Tests.Integration.Connection
{
    /// <summary>
    ///     Real-world case (2026-07-31): a production ASE server declares <c>cp850</c> as its charset
    ///     (the typical default for Windows ASE installs), but the bytes actually on disk for a given
    ///     table were written by another application in <c>windows-1252</c> — a mismatch that
    ///     previously had to be worked around at the ORM layer (<c>Chiola.EntityFrameworkCore.Ase</c>'s
    ///     <c>FixMisdetectedCharset</c>, read-only). Moved down into the driver itself via the
    ///     <c>ActualCharset</c> connection string keyword, so any consumer of this driver benefits, not
    ///     just EF Core users, and both reads AND writes are covered (the server performs no real
    ///     charset conversion here regardless of what it declares — bytes pass through as sent either
    ///     way — so there's no reason to special-case reads only at this layer).
    /// </summary>
    [TestFixture]
    [Category("basic")]
    public class ActualCharsetTests
    {
        private const string TableName = "AseActualCharsetTest";

        // windows-1252 (like cp850) isn't one of .NET's built-in encodings on modern .NET - needs this
        // provider registered. The driver itself never registers it (confirmed: no reference anywhere
        // in src/AdoNetCore.AseClient) - it's left entirely to the consumer, same as it already was for
        // cp850 (see CharsetTests.cs in this same folder). Worth the driver doing this itself by
        // default at some point, since ActualCharset's whole purpose is targeting exactly these legacy
        // charsets - not done here to keep this change scoped to what was asked.
        static ActualCharsetTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        [SetUp]
        public void SetUp()
        {
            using var connection = new AseConnection(ConnectionStrings.Default);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"IF OBJECT_ID('{TableName}') IS NOT NULL DROP TABLE {TableName}";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"CREATE TABLE {TableName} (Id int PRIMARY KEY, Name varchar(50) NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        [TearDown]
        public void TearDown()
        {
            using var connection = new AseConnection(ConnectionStrings.Default);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"IF OBJECT_ID('{TableName}') IS NOT NULL DROP TABLE {TableName}";
            cmd.ExecuteNonQuery();
        }

        [Test]
        public void WithoutActualCharset_LegacyWindows1252Data_ReadsCorrupted()
        {
            InsertRawWindows1252Bytes(1);

            using var connection = new AseConnection(ConnectionStrings.Default);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Name FROM {TableName} WHERE Id = 1";
            var name = (string)cmd.ExecuteScalar();

            // The server declares cp850 (confirmed for this test instance throughout this project's
            // DECISIONS.md) - decoding genuine windows-1252 bytes with that table produces this exact
            // corruption. This test documents the baseline being fixed, not a desired outcome.
            Assert.AreEqual("San MartÝn 730", name);
        }

        [Test]
        public void WithActualCharset_LegacyWindows1252Data_ReadsCorrectly()
        {
            InsertRawWindows1252Bytes(1);

            using var connection = new AseConnection(ConnectionStrings.ActualCharsetWindows1252);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Name FROM {TableName} WHERE Id = 1";
            var name = (string)cmd.ExecuteScalar();

            Assert.AreEqual("San Martín 730", name);
        }

        [Test]
        public void WithActualCharset_NewlyWrittenData_RoundTripsCorrectly()
        {
            const string original = "Configuración - Año - Ñandú - áéíóú";

            using (var connection = new AseConnection(ConnectionStrings.ActualCharsetWindows1252))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"INSERT INTO {TableName} VALUES (2, @name)";
                cmd.Parameters.Add(new AseParameter("@name", original));
                cmd.ExecuteNonQuery();
            }

            using (var connection = new AseConnection(ConnectionStrings.ActualCharsetWindows1252))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT Name FROM {TableName} WHERE Id = 2";
                var name = (string)cmd.ExecuteScalar();

                Assert.AreEqual(original, name);
            }
        }

        // Inserts the raw windows-1252 bytes for "San Martín 730" (í = 0xED) directly via a binary
        // parameter + CONVERT, bypassing the driver's own string encoding entirely - simulates data
        // written by another application under a different charset assumption than this driver uses.
        private static void InsertRawWindows1252Bytes(int id)
        {
            using var connection = new AseConnection(ConnectionStrings.Default);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {TableName} (Id, Name) VALUES ({id}, CONVERT(varchar(50), @raw))";
            var rawBytes = new byte[] { 0x53, 0x61, 0x6E, 0x20, 0x4D, 0x61, 0x72, 0x74, 0xED, 0x6E, 0x20, 0x37, 0x33, 0x30 };
            cmd.Parameters.Add(new AseParameter("@raw", rawBytes));
            cmd.ExecuteNonQuery();
        }
    }
}
