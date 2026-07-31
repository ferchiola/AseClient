namespace AdoNetCore.AseClient.Interface
{
    internal interface IConnectionPool
    {
        /// <summary>
        /// Attempt to reserve an internal connection in the pool for use.
        /// Event notifier must be provided so that messages emitted by the server during login can be captured externally
        /// </summary>
        IInternalConnection Reserve(IInfoMessageEventNotifier eventNotifier);

        /// <summary>
        /// Release a used internal connection back into the pool for reuse or replacement
        /// </summary>
        void Release(IInternalConnection connection);

        /// <summary>
        /// Closes every connection currently idle in the pool. Any connection that is currently
        /// checked out (in use) is closed instead of returned to the pool the next time it is
        /// released, rather than being reused. Mirrors <c>SqlConnection.ClearPool</c>.
        /// </summary>
        void Clear();

        /// <summary>
        /// The number of connections in the pool.
        /// </summary>
        int PoolSize { get; }

        /// <summary>
        /// The number of connections available in the pool.
        /// </summary>
        int Available { get; }
    }
}
