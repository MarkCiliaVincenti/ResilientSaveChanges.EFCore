using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ResilientSaveChanges.EFCore
{
    public static class ResilientSaveChangesConfig
    {
        private static SemaphoreSlim _semaphoreSlim;

        private static int? _concurrentSaveChangesLimit;
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

        public static ILogger Logger { get; set; }
        public static int? LoggerWarnLongRunning { get; set; }

        public static async Task ResilientSaveChangesAsync<T>(this T context) where T : DbContext
        {
            if (_semaphoreSlim != null)
            {
                await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            }
            try
            {
                await ResilientTransaction<T>.New(context).ExecuteAsync(async () =>
                {
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (_semaphoreSlim != null)
                {
                    _semaphoreSlim.Release();
                }
            }
        }

        private class ResilientTransaction<T> where T : DbContext
        {
            private readonly T _context;

            private ResilientTransaction(T context) => _context = context ?? throw new ArgumentNullException(nameof(context));

            public static ResilientTransaction<T> New(T context) => new(context);

            public async Task ExecuteAsync(Func<Task> action)
            {
                Stopwatch stopWatch = null;
                if (LoggerWarnLongRunning.HasValue)
                {
                    stopWatch = new Stopwatch();
                    stopWatch.Start();
                }
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    await action().ConfigureAwait(false);
                    await transaction.CommitAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
                if (LoggerWarnLongRunning.HasValue)
                {
                    stopWatch.Stop();
                    if (stopWatch.ElapsedMilliseconds >= LoggerWarnLongRunning.Value)
                    {
                        var warning = $"Transaction commit took {stopWatch.ElapsedMilliseconds}ms";
                        if (Logger != null)
                            Logger.LogWarning(warning);
                        else
                            Debug.WriteLine(warning);
                    }
                }
            }
        }
    }
}
