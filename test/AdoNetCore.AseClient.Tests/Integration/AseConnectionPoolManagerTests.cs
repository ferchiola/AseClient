using NUnit.Framework;

namespace AdoNetCore.AseClient.Tests.Integration
{
    [TestFixture]
    [Category("extra")]
    public class AseConnectionPoolManagerTests
    {
        [Test]
        public void NumberOfOpenConnections_NewConnectionWithUnpooledConnectionString_ReturnsZero()
        {
            var unpooledConnectionString = ConnectionStrings.NonPooledUnique;
            var originalNumberOfOpenConnections = AseConnectionPoolManager.NumberOfOpenConnections;

            using (var connection = new AseConnection(unpooledConnectionString))
            {
                connection.Open();

                Assert.AreEqual(originalNumberOfOpenConnections, AseConnectionPoolManager.NumberOfOpenConnections);

                connection.Close();

            }

            Assert.AreEqual(originalNumberOfOpenConnections, AseConnectionPoolManager.NumberOfOpenConnections);
        }

        [Test]
        public void GetConnectionPool_NewConnectionWithUnpooledConnectionString_ReturnsPoolWithSizeZero()
        {
            var unpooledConnectionString = ConnectionStrings.NonPooledUnique;

            using (var connection = new AseConnection(unpooledConnectionString))
            {
                connection.Open();

                Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Size);

                connection.Close();

            }

            Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Size);
        }

        [Test]
        public void NumberOfOpenConnections_NewConnectionWithPooledConnectionString_ReturnsOne()
        {
            var unpooledConnectionString = ConnectionStrings.PooledUnique;
            var originalNumberOfOpenConnections = AseConnectionPoolManager.NumberOfOpenConnections;

            using (var connection = new AseConnection(unpooledConnectionString))
            {
                connection.Open();

                Assert.AreEqual(originalNumberOfOpenConnections + 1, AseConnectionPoolManager.NumberOfOpenConnections);

                connection.Close();

            }

            Assert.AreEqual(originalNumberOfOpenConnections + 1, AseConnectionPoolManager.NumberOfOpenConnections);
        }

        [Test]
        public void GetConnectionPool_NewConnectionWithPooledConnectionString_ReturnsPoolWithSizeOne()
        {
            var unpooledConnectionString = ConnectionStrings.PooledUnique;

            using (var connection = new AseConnection(unpooledConnectionString))
            {
                connection.Open();

                Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Size);

                connection.Close();

            }

            Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Size);
        }

        [Test]
        public void GetConnectionPool_NewConnectionWithPooledConnectionString_ReturnsPoolWithAvailable()
        {
            var unpooledConnectionString = ConnectionStrings.PooledUnique;

            using (var connection = new AseConnection(unpooledConnectionString))
            {
                connection.Open();

                Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Available);

                connection.Close();

            }

            Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(unpooledConnectionString).Available);
        }

        [Test]
        public void ClearPool_ClosesIdlePooledConnection()
        {
            var connectionString = ConnectionStrings.PooledUnique;

            using (var connection = new AseConnection(connectionString))
            {
                connection.Open();
                connection.Close();
            }

            Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(connectionString).Available);

            using (var connection = new AseConnection(connectionString))
            {
                connection.ClearPool();
            }

            Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(connectionString).Available);
        }

        [Test]
        public void ClearPools_ClosesIdlePooledConnectionsAcrossAllPools()
        {
            var connectionStringA = ConnectionStrings.PooledUnique;
            var connectionStringB = ConnectionStrings.PooledUnique;

            using (var connection = new AseConnection(connectionStringA))
            {
                connection.Open();
                connection.Close();
            }

            using (var connection = new AseConnection(connectionStringB))
            {
                connection.Open();
                connection.Close();
            }

            Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(connectionStringA).Available);
            Assert.AreEqual(1, AseConnectionPoolManager.GetConnectionPool(connectionStringB).Available);

            AseConnection.ClearPools();

            Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(connectionStringA).Available);
            Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(connectionStringB).Available);
        }

        [Test]
        public void ClearPool_ConnectionInUseWhenCleared_IsClosedInsteadOfPooled_OnceReleased()
        {
            var connectionString = ConnectionStrings.PooledUnique;

            using (var inUse = new AseConnection(connectionString))
            {
                inUse.Open();

                using (var other = new AseConnection(connectionString))
                {
                    other.ClearPool();
                }

                //inUse predates the ClearPool() call above - closing it now shouldn't add it back to the pool.
            }

            Assert.AreEqual(0, AseConnectionPoolManager.GetConnectionPool(connectionString).Available);
        }
    }
}
