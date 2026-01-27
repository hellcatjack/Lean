using System.IO;
using NUnit.Framework;
using QuantConnect.Algorithm.CSharp;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class LeanBridgeSmokeAlgorithmTests
    {
        [Test]
        public void ParsesWatchlistSymbols()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "{\"symbols\":[\"aapl\",\" MSFT \",\"\"]}");

            var symbols = LeanBridgeSmokeAlgorithm.LoadWatchlistSymbols(path);

            CollectionAssert.AreEqual(new[] { "AAPL", "MSFT" }, symbols);
        }
    }
}
