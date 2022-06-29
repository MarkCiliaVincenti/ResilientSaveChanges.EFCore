# ![ResilientSaveChanges.EFCore](https://raw.githubusercontent.com/MarkCiliaVincenti/ResilientSaveChanges.EFCore/master/logo32.png) ResilientSaveChanges.EFCore
 [![GitHub Workflow Status](https://img.shields.io/github/workflow/status/MarkCiliaVincenti/ResilientSaveChanges.EFCore/.NET?logo=github&style=for-the-badge)](https://actions-badge.atrox.dev/MarkCiliaVincenti/ResilientSaveChanges.EFCore/goto?ref=master) [![Nuget](https://img.shields.io/nuget/v/ResilientSaveChanges.EFCore?label=ResilientSaveChanges.EFCore&logo=nuget&style=for-the-badge)](https://www.nuget.org/packages/ResilientSaveChanges.EFCore) [![Nuget](https://img.shields.io/nuget/dt/ResilientSaveChanges.EFCore?logo=nuget&style=for-the-badge)](https://www.nuget.org/packages/ResilientSaveChanges.EFCore)

A library that allows resilient context.SaveChanges / SaveChangesAsync in Entity Framework Core, logging of long-running transactions and limiting of concurrent SaveChanges.

## Installation
The recommended means is to use [NuGet](https://www.nuget.org/packages/ResilientSaveChanges.EFCore), but you could also download the source code from [here](https://github.com/MarkCiliaVincenti/ResilientSaveChanges.EFCore/releases).

## Configuration
```csharp
ResilientSaveChangesConfig.Logger = _logger;
ResilientSaveChangesConfig.LoggerWarnLongRunning = 3_000;
ResilientSaveChangesConfig.ConcurrentSaveChangesLimit = 5;
```

## Setting up an exectution strategy
You now need to create an execution strategy. An example using MySQL and Pomelo:

```csharp
public static class Constants
{
    public const int MAX_RETRY_COUNT = 10;
    public const int MAX_RETRY_DELAY_SECONDS = 6;
    public const int COMMAND_TIMEOUT = 120;
}

public class MyExecutionStrategy : ExecutionStrategy
{
    public MyExecutionStrategy(MyDbContext context) : base(
        context,
        Constants.MAX_RETRY_COUNT,
        TimeSpan.FromSeconds(Constants.MAX_RETRY_DELAY_SECONDS))
    { }

    public MyExecutionStrategy(ExecutionStrategyDependencies dependencies) : base(
        dependencies,
        Constants.MAX_RETRY_COUNT,
        TimeSpan.FromSeconds(Constants.MAX_RETRY_DELAY_SECONDS))
    { }

    public MyExecutionStrategy(MyDbContext context, int maxRetryCount, TimeSpan maxRetryDelay) : base(
        context,
        maxRetryCount,
        maxRetryDelay)
    { }

    protected override bool ShouldRetryOn([NotNull] Exception exception)
    {
        if (exception is MySqlException mySqlException)
        {
            if (mySqlException.IsTransient)
            {
                Debug.WriteLine($"MySqlException transient error detected. Retrying in {Constants.MAX_RETRY_DELAY_SECONDS} seconds");
                return true;
            }
            Debug.WriteLine($"Non-transient MySqlException detected.");
            return false;
        }

        if (exception is DbUpdateException)
        {
            Debug.WriteLine($"DbUpdateException detected. Retrying in {Constants.MAX_RETRY_DELAY_SECONDS} seconds");
            return true;
        }

        Debug.WriteLine($"Error that won't be retried. Type is {exception.GetType()}");
        return false;
    }
}
```

## Using the execution strategy
You now need to set your MySQL or SQL Server options to enable retry on failure and to use your execution strategy. An example using MySQL and Pomelo:

```csharp
services.AddPooledDbContextFactory<MyDbContext>(options =>
{
    options.UseMySql(
        Configuration.GetConnectionString("DefaultConnection"),
        "8.0.29",
        options =>
        {
            options.EnableRetryOnFailure(
                Constants.MAX_RETRY_COUNT, 
                TimeSpan.FromSeconds(Constants.MAX_RETRY_DELAY_SECONDS),
                null);
            options.CommandTimeout(Constants.COMMAND_TIMEOUT);
            options.ExecutionStrategy(s => new MyExecutionStrategy(s));
        }
    ).EnableDetailedErrors();
});
```

## How to use
Now simply replace your `context.SaveChanges();` and `await context.SaveChangesAsync();` with `context.ResilientSaveChanges();` and `context.ResilientSaveChangesAsync();` respectively.

## Credits
Some code has been adapted from code found in .NET Microservices: Architecture for Containerized .NET Applications (de la Torre, Wagner, & Rousos, 2022)