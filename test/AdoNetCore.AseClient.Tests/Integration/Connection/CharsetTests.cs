using System;
using System.Text;
using NUnit.Framework;

namespace AdoNetCore.AseClient.Tests.Integration.Connection
{
    [TestFixture]
    [Category("basic")]
    public class CharsetTests
    {
        private class TestEncodingProvider : EncodingProvider
        {
            public override Encoding GetEncoding(int codepage)
            {
                return null;
            }

            public override Encoding GetEncoding(string name)
            {
                if (string.Equals("cp850", name, StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.GetEncoding(850);
                }

                return null;
            }
        }

        [Test]
        public void OpenConnection_WithCharsetCp850_PlusEncodingProvider_Succeeds()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding.RegisterProvider(new TestEncodingProvider());

            using (var connection = new AseConnection(ConnectionStrings.Cp850))
            {
                connection.Open();
            }
        }

        #if NET_CORE
        // Test case is only relevant to .net core, where CP850 isn't provided out of the box.
        // Framework has implemented it, so case would always fail.
        [Test]
        public void OpenConnection_WithCharsetCp850_NoEncodingProvider_Throws()
        {
            // Encoding.RegisterProvider is process-wide and irreversible - if some other test fixture in
            // this same test run already registered a codepages provider (e.g. ActualCharsetTests, which
            // needs one for windows-1252), cp850 now resolves regardless of what this test does, and the
            // precondition this test relies on no longer holds. Skip rather than fail in that case - this
            // isn't a real regression, just two tests sharing one process's global encoding registry.
            try
            {
                Encoding.GetEncoding("cp850");
                Assert.Ignore("A codepages EncodingProvider is already registered by another test fixture in this run - this test's precondition (no provider registered) isn't met.");
            }
            catch (ArgumentException)
            {
                // expected: cp850 isn't resolvable yet, precondition holds, proceed with the real test
            }

            using (var connection = new AseConnection(ConnectionStrings.Cp850))
            {
                Assert.Throws<AseException>(() => connection.Open());
            }
        }
        #endif
    }
}
