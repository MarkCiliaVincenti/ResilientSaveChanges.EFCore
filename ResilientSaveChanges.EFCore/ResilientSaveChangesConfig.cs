using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ResilientSaveChanges.EFCore;

// Credits: Some code has been adapted from code found in .NET Microservices: Architecture for
// Containerized .NET Applications (de la Torre, Wagner, & Rousos, 2022)

/// <summary>
/// Static configuration for ResilientSaveChanges.EFCore, which also acts as an extension class
/// for <see cref="DbContext"/>.
/// </summary>
public static class ResilientSaveChangesConfig
{
    private static SemaphoreSlim? _semaphoreSlim;

    private static int? _concurrentSaveChangesLimit;

    /// <summary>
    /// Defines how many concurrent ResilientSaveChanges / ResilientSaveChangesAsync can be allowed.
    /// Default (null) means unlimited.
    /// </summary>
    public static int? ConcurrentSaveChangesLimit
    {
        get
        {
            return _concurrentSaveChangesLimit;
        }
        set
        {
            _concurrentSaveChangesLimit = value;
            _semaphoreSlim = value.HasValue ? new(value.Value) : null;
        }
    }

    /// <summary>
    /// <see cref="ILogger"/> instance used for logging long running ResilientSaveChanges /
    /// ResilientSaveChangesAsync. Will use <see cref="Debug.WriteLine(string?)"/> if set to null while
    /// <see cref="LoggerWarnLongRunning"/> has a value.
    /// </summary>
    public static ILogger? Logger { get; set; }

    /// <summary>
    /// The number of milliseconds taken to execute the ResilientSaveChanges / ResilientSaveChangesAsync
    /// that will trigger a logged warning. Default (null) means disabled.
    /// </summary>
    public static int? LoggerWarnLongRunning { get; set; }

    extension<T>(T context) where T : DbContext
    {
        /// <summary>
        /// Resilient synchronous <see cref="DbContext.SaveChanges()"/>.
        /// </summary>
        public void ResilientSaveChanges()
        {
            _semaphoreSlim?.Wait();
            try
            {
                ResilientTransaction<T>.New(context).Execute(context.SaveChanges);
            }
            finally
            {
                _semaphoreSlim?.Release();
            }
        }

        /// <summary>
        /// Resilient asynchronous <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token passed on
        /// to <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.</param>
        public async Task ResilientSaveChangesAsync(
            CancellationToken cancellationToken = default
        )
        {
            if (_semaphoreSlim != null)
            {
                await _semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                await ResilientTransaction<T>.New(context).ExecuteAsync(
                    async () => await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken
                ).ConfigureAwait(false);
            }
            finally
            {
                _semaphoreSlim?.Release();
            }
        }
    }

    private class ResilientTransaction<T> where T : DbContext
    {
        private readonly T _context;

        private ResilientTransaction(T context)
            => _context = context ?? throw new ArgumentNullException(nameof(context));

        public static ResilientTransaction<T> New(T context) => new(context);

        public void Execute(Func<int> action)
        {
            if (LoggerWarnLongRunning == null)
            {
                var execStrategy = _context.Database.CreateExecutionStrategy();
                execStrategy.Execute(() =>
                {
                    var transaction = _context.Database.BeginTransaction();
                    action();
                    transaction.Commit();
                });

                return;
            }

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var strategy = _context.Database.CreateExecutionStrategy();
            strategy.Execute(() =>
            {
                var transaction = _context.Database.BeginTransaction();
                action();
                transaction.Commit();
            });

            stopWatch.Stop();
            if (stopWatch.ElapsedMilliseconds >= LoggerWarnLongRunning.Value)
            {
                var elapsed = stopWatch.ElapsedMilliseconds;
                if (Logger != null)
                {
                    Logger.LogWarning("Transaction commit took {ElapsedMilliseconds}ms", elapsed);
                }
                else
                {
                    Debug.WriteLine($"Transaction commit took {elapsed}ms");
                }
            }
        }

        public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            if (LoggerWarnLongRunning == null)
            {
                var execStrategy = _context.Database.CreateExecutionStrategy();
                await execStrategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                    await action().ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

                return;
            }

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                await action().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

            stopWatch.Stop();
            if (stopWatch.ElapsedMilliseconds >= LoggerWarnLongRunning.Value)
            {
                var elapsed = stopWatch.ElapsedMilliseconds;
                if (Logger != null)
                {
                    Logger.LogWarning("Transaction commit took {ElapsedMilliseconds}ms", elapsed);
                }
                else
                {
                    Debug.WriteLine($"Transaction commit took {elapsed}ms");
                }
            }
        }
    }
}
