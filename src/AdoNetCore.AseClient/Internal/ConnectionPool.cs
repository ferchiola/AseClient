using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AdoNetCore.AseClient.Interface;

namespace AdoNetCore.AseClient.Internal
{
    internal class ConnectionPool : IConnectionPool
    {
        //concurrency-related members
        private readonly object _mutex = new object();
        private readonly BlockingCollection<IInternalConnection> _available;

        /// <summary>
        /// The number of connections in the pool.
        /// </summary>
        public int PoolSize { get; private set; }

        /// <summary>
        /// Bumped by <see cref="Clear"/>. Connections stamp their own <see cref="IInternalConnection.Generation"/>
        /// when created (see <see cref="CreateNewPooledConnection"/>); a connection whose generation no longer
        /// matches this value predates the last <see cref="Clear"/> call and gets closed instead of pooled the
        /// next time it's released (see <see cref="Release"/>) — even though nothing is actually wrong with it.
        /// Always read/written under <see cref="_mutex"/>, same as <see cref="PoolSize"/>.
        /// </summary>
        private int _generation;

        //regular members
        private readonly IConnectionParameters _parameters;
        private readonly IInternalConnectionFactory _connectionFactory;

        /// <summary>
        /// The number of connections available in the pool.
        /// </summary>
        public int Available => _available.Count;

        // Timer for the idle/lifetime sweep started below - kept alive for the lifetime of the pool (which
        // itself lives forever in ConnectionPoolManager's static dictionary, same as every other pool
        // resource here; there's no pool teardown path to hook a Dispose into).
        private readonly Timer _idleSweepTimer;
        private readonly int _idleSweepIntervalMs;

        public ConnectionPool(IConnectionParameters parameters, IInternalConnectionFactory connectionFactory)
        {
            _parameters = parameters;
            _connectionFactory = connectionFactory;
            _available = new BlockingCollection<IInternalConnection>();

            PoolSize = 0;

            if (_parameters.Pooling && _parameters.MinPoolSize > 0)
            {
                Task.Run(TryFillPoolToMinSize);
                Logger.Instance?.WriteLine("Pool fill task started");
            }

            if (_parameters.Pooling && (_parameters.ConnectionIdleTimeout > 0 || _parameters.ConnectionLifetime > 0))
            {
                // Tie the sweep frequency to whichever configured timeout is smaller, so a short
                // ConnectionIdleTimeout is actually enforced promptly rather than just eventually - with a
                // floor so a very small timeout (e.g. 1s, common in tests) doesn't spin the sweep too tight.
                var relevantTimeouts = new List<int>(2);
                if (_parameters.ConnectionIdleTimeout > 0)
                {
                    relevantTimeouts.Add(_parameters.ConnectionIdleTimeout);
                }
                if (_parameters.ConnectionLifetime > 0)
                {
                    relevantTimeouts.Add(_parameters.ConnectionLifetime);
                }

                var intervalSeconds = Math.Max(2, relevantTimeouts.Min() / 2);
                _idleSweepIntervalMs = intervalSeconds * 1000;
                _idleSweepTimer = new Timer(SweepIdleConnections, null, _idleSweepIntervalMs, Timeout.Infinite);
                Logger.Instance?.WriteLine($"Idle connection sweep started, interval {intervalSeconds}s");
            }
        }

        /// <summary>
        /// Proactively evicts idle pooled connections that have exceeded <see cref="IConnectionParameters.ConnectionIdleTimeout"/>
        /// or <see cref="IConnectionParameters.ConnectionLifetime"/>, instead of leaving that check to only
        /// happen lazily whenever a connection is next reserved (which never happens at all if the pool goes
        /// idle - see DECISIONS.md, "no hay limpieza proactiva de conexiones idle en el pool").
        /// </summary>
        private void SweepIdleConnections(object state)
        {
            try
            {
                Logger.Instance?.WriteLine($"{nameof(SweepIdleConnections)} start");

                var now = DateTime.UtcNow;
                var toKeep = new List<IInternalConnection>();
                var removedCount = 0;

                // Drain everything currently idle, individually via non-blocking TryTake - the brief window
                // where this makes the pool look emptier than it is to a concurrent Reserve() is benign (at
                // worst, one extra connection gets created instead of reusing one that's about to be put
                // back below) and self-corrects immediately.
                while (_available.TryTake(out var connection))
                {
                    if (ShouldRemoveAndReplace(connection, now))
                    {
                        RemoveAndReplace(connection);
                        removedCount++;
                    }
                    else
                    {
                        toKeep.Add(connection);
                    }
                }

                foreach (var connection in toKeep)
                {
                    AddToPool(connection);
                }

                if (removedCount > 0)
                {
                    Logger.Instance?.WriteLine($"{nameof(SweepIdleConnections)} closed {removedCount} idle connection(s)");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance?.WriteLine($"{nameof(SweepIdleConnections)} exception: {ex}");
            }
            finally
            {
                // Single-shot Timer, rescheduled here rather than a repeating Timer - avoids overlapping
                // sweeps if one run ever takes longer than the interval (e.g. a huge pool).
                _idleSweepTimer?.Change(_idleSweepIntervalMs, Timeout.Infinite);
            }
        }

        public IInternalConnection Reserve(IInfoMessageEventNotifier eventNotifier)
        {
            try
            {
                using (var src = new CancellationTokenSource())
                {
                    var t = src.Token;
                    src.CancelAfter(TimeSpan.FromSeconds(_parameters.LoginTimeout));

                    var task = _parameters.Pooling
                        ? ReservePooledConnection(t, eventNotifier)
                        : _connectionFactory.GetNewConnection(t, eventNotifier);

                    task.Wait(t);
                    var connection = task.Result;

                    if (connection == null)
                    {
                        throw new OperationCanceledException();
                    }

                    try
                    {
                        connection.ChangeDatabase(_parameters.Database);
                        connection.SetTextSize(_parameters.TextSize);
                        connection.NamedParameters = _parameters.NamedParameters;

                        if (_parameters.AnsiNull)
                        {
                            connection.SetAnsiNull(_parameters.AnsiNull);
                        }
                    }
                    catch (Exception)
                    {
                        if(_parameters.Pooling)
                        {
                            RemoveConnection(connection);
                        }
                        else
                        {
                            connection?.Dispose();
                        }
                        throw;
                    }

                    return connection;
                }
            }
            catch (AggregateException ae)
            {
                if (ae.InnerException is OperationCanceledException)
                {
                    throw GetTimedOutAseException(_parameters.Pooling);
                }

                if (ae.InnerException is AseException)
                {
                    ExceptionDispatchInfo.Capture(ae.InnerException).Throw();
                    throw;
                }

                throw new AseException(ae.InnerException);
            }
            catch (OperationCanceledException)
            {
                throw GetTimedOutAseException(_parameters.Pooling);
            }
            catch (AseException)
            {
                throw;
            }
            catch (SocketException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AseException(ex);
            }
        }

        private static AseException GetTimedOutAseException(bool poolingEnabled)
        {
            return poolingEnabled
                ? new AseException("Pool timed out trying to reserve a connection")
                : new AseException("Timed out trying to establish a connection");
        }

        private async Task<IInternalConnection> ReservePooledConnection(CancellationToken cancellationToken, IInfoMessageEventNotifier eventNotifier)
        {
            return FetchIdlePooledConnection(eventNotifier)
                   ?? (CheckAndIncrementPoolSize()
                       //there's room in the pool! create new connection and return it
                       ? await CreateNewPooledConnection(cancellationToken, eventNotifier)
                       //pool's full, wait for something to release a connection
                       : WaitForPooledConnection(cancellationToken, eventNotifier));
        }

        private IInternalConnection FetchIdlePooledConnection(IInfoMessageEventNotifier eventNotifier)
        {
            var now = DateTime.UtcNow;
            while (_available.TryTake(out var connection))
            {
                if (ShouldRemoveAndReplace(connection, now))
                {
                    RemoveAndReplace(connection);
                    continue;
                }

                if (_parameters.PingServer && !connection.Ping())
                {
                    RemoveAndReplace(connection);
                    continue;
                }

                Logger.Instance?.WriteLine($"{nameof(FetchIdlePooledConnection)} returned idle connection");
                connection.EventNotifier = eventNotifier;
                return connection;
            }

            Logger.Instance?.WriteLine($"{nameof(FetchIdlePooledConnection)} found no idle connection");
            return null;
        }

        private async Task<IInternalConnection> CreateNewPooledConnection(CancellationToken cancellationToken = new CancellationToken(), IInfoMessageEventNotifier eventNotifier = null)
        {
            try
            {
                Logger.Instance?.WriteLine($"{nameof(CreateNewPooledConnection)} start");
                var connection = await _connectionFactory.GetNewConnection(cancellationToken, eventNotifier);

                lock (_mutex)
                {
                    connection.Generation = _generation;
                }

                return connection;
            }
            catch
            {
                Logger.Instance?.WriteLine($"{nameof(CreateNewPooledConnection)} failed");
                RemoveConnection();
                throw;
            }
        }

        private IInternalConnection WaitForPooledConnection(CancellationToken cancellationToken, IInfoMessageEventNotifier eventNotifier)
        {
            Logger.Instance?.WriteLine($"{nameof(WaitForPooledConnection)} start");
            try
            {
                var conn = _available.Take(cancellationToken);
                Logger.Instance?.WriteLine($"{nameof(WaitForPooledConnection)} found connection");
                conn.EventNotifier = eventNotifier;
                return conn;
            }
            catch (OperationCanceledException)
            {
                Logger.Instance?.WriteLine($"{nameof(WaitForPooledConnection)} timed out waiting for connection");
                throw;
            }
        }

        private async Task TryFillPoolToMinSize()
        {
            Logger.Instance?.WriteLine($"{nameof(TryFillPoolToMinSize)} begin");
            while (CheckAndIncrementPoolSize(true))
            {
                try
                {
                    AddToPool(await CreateNewPooledConnection(/*Since this is an internal optimisation, there's no point supplying a cancellation token or event notifier*/));
                    Logger.Instance?.WriteLine($"{nameof(TryFillPoolToMinSize)} added new internal connection");
                }
                catch(Exception ex)
                {
                    Logger.Instance?.WriteLine($"{nameof(TryFillPoolToMinSize)} exception: {ex}");
                }
            }
            Logger.Instance?.WriteLine($"{nameof(TryFillPoolToMinSize)} end");
        }

        private async Task TryReplaceConnection()
        {
            Logger.Instance?.WriteLine();
            if (CheckAndIncrementPoolSize(true))
            {
                try
                {
                    AddToPool(await CreateNewPooledConnection(/*Since this is an internal optimisation, there's no point supplying a cancellation token or event notifier*/));
                    Logger.Instance?.WriteLine($"{nameof(TryReplaceConnection)} added new internal connection");
                }
                catch (Exception ex)
                {
                    Logger.Instance?.WriteLine($"{nameof(TryReplaceConnection)} exception: {ex}");
                }
            }
        }

        private bool CheckAndIncrementPoolSize(bool mustBeBelowMin = false)
        {
            lock (_mutex)
            {
                if (PoolSize < (mustBeBelowMin ? _parameters.MinPoolSize : _parameters.MaxPoolSize))
                {
                    PoolSize++;
                    return true;
                }

                return false;
            }
        }

        private void RemoveConnection(IInternalConnection connection = null)
        {
            lock (_mutex)
            {
                try
                {
                    connection?.Dispose();
                }
                finally
                {
                    if (PoolSize > 0)
                    {
                        PoolSize--;
                    }
                }
            }
        }

        private void RemoveAndReplace(IInternalConnection connection)
        {
            RemoveConnection(connection);
            Task.Run(TryReplaceConnection);
        }

        private bool ShouldRemoveAndReplace(IInternalConnection connection, DateTime now)
        {
            return connection.IsDoomed
                   || (_parameters.ConnectionLifetime > 0 && _parameters.ConnectionLifetime < (now - connection.Created).TotalSeconds)
                   || (_parameters.ConnectionIdleTimeout > 0 && _parameters.ConnectionIdleTimeout < (now - connection.LastActive).TotalSeconds);
        }

        public void Release(IInternalConnection connection)
        {
            if (!_parameters.Pooling)
            {
                connection?.Dispose();
                return;
            }

            if (connection == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            bool staleGeneration;
            lock (_mutex)
            {
                staleGeneration = connection.Generation != _generation;
            }

            if (ShouldRemoveAndReplace(connection, now) || staleGeneration)
            {
                RemoveAndReplace(connection);
                Logger.Instance?.WriteLine("Released connection was removed. Creation of a replacement was tasked.");
                return;
            }

            AddToPool(connection);
        }

        private void AddToPool(IInternalConnection connection)
        {
            _available.Add(connection);
            Logger.Instance?.WriteLine("Released connection was placed in available queue");
        }

        /// <summary>
        /// See <see cref="IConnectionPool.Clear"/>.
        /// </summary>
        public void Clear()
        {
            lock (_mutex)
            {
                _generation++;
            }

            Logger.Instance?.WriteLine($"{nameof(Clear)} closing idle connections and bumping generation");

            // Connections currently checked out aren't reachable from here — they get closed instead of
            // pooled the next time Release() is called on them, via the generation check above.
            while (_available.TryTake(out var connection))
            {
                RemoveConnection(connection);
            }
        }
    }
}
